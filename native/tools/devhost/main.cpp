// devhost.nro: opened once from hbmenu, it takes each .nro nxlink sends from the PC and runs it; a
// program built with DevHostReturn comes back here when it exits, so a new build runs without going
// back to hbmenu and pressing Y. From the PC, as to hbmenu's netloader:
//
//   nxlink -s -a <Switch IP> [-p openshiina/<name>.nro] <name>.nro [arguments]
//
// It speaks hbmenu's netloader protocol on the same port (and answers nxlink's search without -a):
// the .nro is saved under sdmc:/switch (-p gives the path there) and started through hbloader with
// its arguments, then "_DEVHOST_=<this .nro>" and the PC's address for nxlink -s, as hbmenu passes
// it. A connection that sends something else is closed and the next one waited for. + goes back to
// hbmenu.

// gethostid and timeval: BSD and POSIX, which a strict C++20 build leaves out unless asked for
#define _DEFAULT_SOURCE

#include <cerrno>
#include <cinttypes>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include <arpa/inet.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <switch.h>
#include <unistd.h>

#include "miniz.h"
#include "platform/Files.h"

using namespace openshiina;

namespace {

constexpr uint16_t kServerPort = 28280; // where nxlink sends (NXLINK_SERVER_PORT)
constexpr uint16_t kClientPort = 28771; // where nxlink waits for the answer to its search
constexpr const char* kRoot = "sdmc:/switch";
constexpr const char* kSelf = "sdmc:/switch/openshiina/devhost.nro";
constexpr size_t kChunk = 16 * 1024; // the largest compressed chunk nxlink sends

void Log(const char* format, ...)
{
    va_list args;
    va_start(args, format);
    std::vprintf(format, args);
    va_end(args);
    consoleUpdate(nullptr);
}

bool ReceiveAll(int sock, void* buffer, size_t size)
{
    auto* at = static_cast<uint8_t*>(buffer);
    while (size)
    {
        ssize_t got = recv(sock, at, size, 0);
        if (got <= 0)
            return false;
        at += got;
        size -= size_t(got);
    }
    return true;
}

bool SendAll(int sock, const void* buffer, size_t size)
{
    auto* at = static_cast<const uint8_t*>(buffer);
    while (size)
    {
        ssize_t sent = send(sock, at, size, 0);
        if (sent <= 0)
            return false;
        at += sent;
        size -= size_t(sent);
    }
    return true;
}

bool ReceiveInt(int sock, int32_t& value) { return ReceiveAll(sock, &value, sizeof value); }
bool SendInt(int sock, int32_t value) { return SendAll(sock, &value, sizeof value); }

bool EndsWithNro(const std::string& path)
{
    if (path.size() < 4)
        return false;
    std::string extension = path.substr(path.size() - 4);
    for (char& c : extension)
        c = char(c >= 'A' && c <= 'Z' ? c + 32 : c);
    return extension == ".nro";
}

/// The file under sdmc:/switch for the path nxlink sends; empty when it would be outside that folder
/// or is not an .nro.
std::string TargetPath(const std::string& sent)
{
    std::string path = kRoot;
    size_t start = 0;
    while (start <= sent.size())
    {
        size_t end = sent.find_first_of("/\\", start);
        if (end == std::string::npos)
            end = sent.size();
        std::string part = sent.substr(start, end - start);
        if (part == "..")
            return {};
        if (!part.empty() && part != ".")
            path += "/" + part;
        start = end + 1;
    }
    return EndsWithNro(path) ? path : std::string();
}

struct Upload
{
    std::string path;
    std::vector<std::string> arguments;
};

/// The rest of an upload once its file is open: the zlib stream in chunks, each after its size,
/// written to file; true when it ends with exactly size bytes.
bool Inflate(int sock, std::FILE* file, size_t size, std::string& error)
{
    mz_stream stream{};
    if (mz_inflateInit(&stream) != MZ_OK)
    {
        error = "inflateInit failed";
        return false;
    }
    std::vector<uint8_t> in(kChunk), out(kChunk);
    size_t total = 0;
    int status = MZ_OK;
    while (status != MZ_STREAM_END)
    {
        int32_t chunk = 0;
        if (!ReceiveInt(sock, chunk) || chunk <= 0 || size_t(chunk) > kChunk || !ReceiveAll(sock, in.data(), size_t(chunk)))
        {
            error = "the connection stopped or sent a bad chunk";
            break;
        }
        stream.next_in = in.data();
        stream.avail_in = unsigned(chunk);
        do
        {
            stream.next_out = out.data();
            stream.avail_out = unsigned(kChunk);
            status = mz_inflate(&stream, MZ_NO_FLUSH);
            // MZ_BUF_ERROR: the output was full and nothing is left to inflate in this chunk
            if (status != MZ_OK && status != MZ_STREAM_END && status != MZ_BUF_ERROR)
            {
                error = "inflate error " + std::to_string(status);
                break;
            }
            size_t have = kChunk - stream.avail_out;
            if (std::fwrite(out.data(), 1, have, file) != have)
            {
                error = "cannot write the file";
                status = MZ_DATA_ERROR;
                break;
            }
            total += have;
            // Unlike zlib's, miniz's inflate returns after giving out what it had left over from the
            // call before, with input still unread: called until the chunk is used up
        } while (status != MZ_STREAM_END && status != MZ_BUF_ERROR && (stream.avail_in > 0 || stream.avail_out == 0));
        if (!error.empty())
            break;
    }
    mz_inflateEnd(&stream);
    if (error.empty() && total != size)
        error = "got " + std::to_string(total) + " bytes of " + std::to_string(size);
    return error.empty();
}

/// One upload as hbmenu's netloader takes it: the path's length and the path, the file's size, a
/// response (0, or -1 when the file cannot be made), the compressed file, a second response, then the
/// arguments, each ending with a 0. The file is written next to its place and moved there at the end.
bool Receive(int sock, Upload& upload, std::string& error)
{
    int32_t nameLength = 0;
    if (!ReceiveInt(sock, nameLength) || nameLength <= 0 || nameLength > 1024)
    {
        error = "not an nxlink upload";
        return false;
    }
    std::string name(size_t(nameLength), '\0');
    int32_t size = 0;
    if (!ReceiveAll(sock, name.data(), name.size()) || !ReceiveInt(sock, size) || size <= 0)
    {
        error = "not an nxlink upload";
        return false;
    }
    upload.path = TargetPath(name);
    std::string part = upload.path + ".part";
    std::FILE* file = nullptr;
    if (upload.path.empty())
        error = "not an .nro under sdmc:/switch: " + name;
    else if (!Files::CreateDirectories(Files::DirectoryName(upload.path)) || !(file = Files::Open(part, "wb")))
        error = "cannot write " + part + ": " + std::strerror(errno);
    if (!SendInt(sock, file ? 0 : -1) || !file)
    {
        if (file)
            std::fclose(file);
        if (error.empty())
            error = "the connection stopped";
        return false;
    }
    Log("Receiving %s, %d bytes\n", upload.path.c_str(), size);

    bool ok = Inflate(sock, file, size_t(size), error);
    ok = std::fclose(file) == 0 && ok;
    int32_t argumentsLength = 0;
    std::string arguments;
    if (ok && (!SendInt(sock, 0) || !ReceiveInt(sock, argumentsLength) || argumentsLength < 0 || argumentsLength > 4096))
    {
        error = "no arguments after the file";
        ok = false;
    }
    if (ok)
    {
        arguments.resize(size_t(argumentsLength));
        if (!ReceiveAll(sock, arguments.data(), arguments.size()))
        {
            error = "no arguments after the file";
            ok = false;
        }
    }
    // The SD card's file system does not move a file onto another one
    if (ok)
    {
        unlink(upload.path.c_str());
        if (rename(part.c_str(), upload.path.c_str()) != 0)
        {
            error = "cannot move " + part + ": " + std::strerror(errno);
            ok = false;
        }
    }
    if (!ok)
    {
        unlink(part.c_str());
        return false;
    }
    for (size_t start = 0; start < arguments.size();)
    {
        size_t end = arguments.find('\0', start);
        if (end == std::string::npos)
            end = arguments.size();
        upload.arguments.push_back(arguments.substr(start, end - start));
        start = end + 1;
    }
    return true;
}

std::string Quoted(const std::string& argument) { return "\"" + argument + "\""; }

/// This .nro's path as hbloader takes it, for the programs it runs to come back to.
std::string SelfPath(int argc, char** argv)
{
    if (argc < 1 || !argv[0] || !EndsWithNro(argv[0]))
        return kSelf;
    std::string path = argv[0];
    return path.rfind("sdmc:", 0) == 0 ? path : "sdmc:" + path;
}

int Listen(int type)
{
    int sock = socket(AF_INET, type, 0);
    if (sock < 0)
        return -1;
    int reuse = 1;
    setsockopt(sock, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof reuse);
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_ANY);
    address.sin_port = htons(kServerPort);
    if (bind(sock, reinterpret_cast<sockaddr*>(&address), sizeof address) != 0
        || (type == SOCK_STREAM && listen(sock, 4) != 0)
        || fcntl(sock, F_SETFL, fcntl(sock, F_GETFL) | O_NONBLOCK) != 0)
    {
        close(sock);
        return -1;
    }
    return sock;
}

} // namespace

int main(int argc, char** argv)
{
    consoleInit(nullptr);
    padConfigureInput(1, HidNpadStyleSet_NpadStandard);
    PadState pad;
    padInitializeDefault(&pad);

    std::string self = SelfPath(argc, argv);
    bool network = false, started = false;
    int udp = -1, tcp = -1;
    if (!envHasNextLoad())
        Log("devhost needs hbmenu's loader (hbloader) to start other programs.\n");
    else if (!(network = R_SUCCEEDED(socketInitializeDefault())))
        Log("No network.\n");
    else if ((udp = Listen(SOCK_DGRAM)) < 0 || (tcp = Listen(SOCK_STREAM)) < 0)
        Log("Cannot listen on port %d: %s\n", kServerPort, std::strerror(errno));
    else
    {
        // No dimming or sleep while waiting, nor while the programs it starts run (they set it again
        // in DevHostReturn): sleep would close the network, and after a long wait the Switch would
        // sleep the moment it was allowed to
        appletSetMediaPlaybackState(true);
        in_addr host{};
        host.s_addr = gethostid();
        Log("devhost %s\nWaiting on %s:%d. From the PC:\n  nxlink -s -a %s <name>.nro\n+ goes back to hbmenu.\n\n",
            self.c_str(), inet_ntoa(host), kServerPort, inet_ntoa(host));
    }

    while (appletMainLoop())
    {
        padUpdate(&pad);
        if (padGetButtonsDown(&pad) & HidNpadButton_Plus)
            break;
        if (udp >= 0)
        {
            // nxlink without -a looks for the Switch with "nxboot" and waits for "bootnx"
            char message[64];
            sockaddr_in from{};
            socklen_t fromLength = sizeof from;
            ssize_t got = recvfrom(udp, message, sizeof message, 0, reinterpret_cast<sockaddr*>(&from), &fromLength);
            if (got >= 6 && std::memcmp(message, "nxboot", 6) == 0)
            {
                from.sin_port = htons(kClientPort);
                sendto(udp, "bootnx", 6, 0, reinterpret_cast<sockaddr*>(&from), sizeof from);
            }
        }
        sockaddr_in remote{};
        socklen_t remoteLength = sizeof remote;
        int sock = tcp >= 0 ? accept(tcp, reinterpret_cast<sockaddr*>(&remote), &remoteLength) : -1;
        if (sock < 0)
        {
            consoleUpdate(nullptr);
            svcSleepThread(50'000'000);
            continue;
        }
        // Blocking, with a time limit, so that a connection which sends nothing does not hold devhost
        fcntl(sock, F_SETFL, fcntl(sock, F_GETFL) & ~O_NONBLOCK);
        timeval limit{10, 0};
        setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, &limit, sizeof limit);
        setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, &limit, sizeof limit);
        Upload upload;
        std::string error;
        bool received = Receive(sock, upload, error);
        close(sock);
        if (!received)
        {
            Log("From %s: %s\n", inet_ntoa(remote.sin_addr), error.c_str());
            continue;
        }
        std::string line = Quoted(upload.path);
        for (const auto& argument : upload.arguments)
            line += " " + Quoted(argument);
        char nxlink[17];
        std::snprintf(nxlink, sizeof nxlink, "%08" PRIx32 "_NXLINK_", uint32_t(remote.sin_addr.s_addr));
        line += " " + Quoted("_DEVHOST_=" + self) + " " + Quoted(nxlink);
        Result rc = envSetNextLoad(upload.path.c_str(), line.c_str());
        if (R_FAILED(rc))
        {
            Log("Cannot start %s: 2%03d-%04d\n", upload.path.c_str(), R_MODULE(rc), R_DESCRIPTION(rc));
            continue;
        }
        Log("Starting %s\n", upload.path.c_str());
        started = true;
        break;
    }

    if (tcp >= 0)
    {
        close(tcp);
        if (!started)
            appletSetMediaPlaybackState(false);
    }
    if (udp >= 0)
        close(udp);
    if (network)
        socketExit();
    consoleExit(nullptr);
    return 0;
}
