// devhost.nro: opened once from hbmenu, it takes each .nro nxlink sends from the PC and runs it; a
// program built with DevHostReturn comes back here when it exits, so a new build runs without going
// back to hbmenu and pressing Y. From the PC, as to hbmenu's netloader:
//
//   nxlink -s -a <Switch IP> [-p openshiina/<name>.nro] <name>.nro [arguments]
//
// It speaks hbmenu's netloader protocol on the same port (and answers nxlink's search without -a):
// the .nro is saved under sdmc:/switch (-p gives the path there) and started through hbloader with
// its arguments, then "_DEVHOST_=<this .nro>" and the PC's address for nxlink -s, as hbmenu passes
// it. A path that starts with '@' is a file for sdmc:/OpenShiina (a game's files sent over the
// network: -p @Games/<folder>/<file>): it is saved there and the next upload waited for. One that
// starts with '+' is an .nro saved under sdmc:/switch without being started (-p +openshiina/OpenShiina.nro:
// a new build put in place, for the OpenShiina title to start next time).
// A connection that starts with "GETF" in place of a path's length reads instead (tools/devhost/
// devget.ps1 on the PC): the path's length and a path of the SD card (sdmc:/...), then devhost
// answers 0, the file's size (8 bytes) and the file as it is, or 1, a length and the folder's
// entries ("D" or "F", a tab, the size, a tab, the name, a line each), or -1 when there is neither.
// "DELF" in its place deletes a file under sdmc:/OpenShiina (the files sent with '@'; never a
// folder, nothing elsewhere): devhost answers 0, or -1 when it cannot.
// "PUTF" writes a file under sdmc:/OpenShiina as it is, not compressed (tools/devhost/devput.ps1: a
// game's files, much faster than nxlink's zlib chunks of 16 KB): the path's length, a path under
// sdmc:/OpenShiina as with '@' (Games/<folder>/<file>), the size (8 bytes); devhost answers 0 (or -1),
// takes the bytes, written next to their place (on a thread of their own, while the next arrive) and
// moved there at the end, and answers 0 (or -1).
// "RUNF" starts an .nro already on the SD card, with arguments (tools/devhost/devrun.ps1: a test run of
// the player, no 13 MB upload for each): the path's length, a path under sdmc:/switch as nxlink's
// (openshiina/OpenShiina.nro), the arguments' length and the arguments, each ended by a zero byte;
// devhost answers 0 and starts it, or -1 when there is no such .nro.
// The sockets have large TCP buffers: with libnx's 64 KB a transfer over Wi-Fi stopped at ~12 Mbit/s.
// A connection that sends something else is closed and the next one waited for. + goes back to
// hbmenu.

// gethostid and timeval: BSD and POSIX, which a strict C++20 build leaves out unless asked for
#define _DEFAULT_SOURCE

#include <algorithm>
#include <cerrno>
#include <cinttypes>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include <arpa/inet.h>
#include <dirent.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/stat.h>
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
constexpr const char* kDataRoot = "sdmc:/OpenShiina";
constexpr const char* kSelf = "sdmc:/switch/openshiina/devhost.nro";
constexpr size_t kChunk = 16 * 1024; // the largest compressed chunk nxlink sends
constexpr int32_t kGetMagic = 0x46544547; // "GETF" as the first 4 bytes, little-endian
constexpr int32_t kDeleteMagic = 0x464C4544; // "DELF"
constexpr int32_t kPutMagic = 0x46545550; // "PUTF"
constexpr int32_t kRunMagic = 0x464E5552; // "RUNF"
// A file of PUTF is written to the SD card a block at a time, while the next blocks arrive
constexpr size_t kWriteBlock = 4 * 1024 * 1024;
constexpr int kWriteBlocks = 3;

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

/// The file under sdmc:/switch for the path nxlink sends, or under sdmc:/OpenShiina for one that starts
/// with '@' (a data file); empty when it would be outside that folder or a program is not an .nro.
/// A path that starts with '+' is an .nro under sdmc:/switch too, saved and not started.
std::string TargetPath(const std::string& sent)
{
    bool data = !sent.empty() && sent[0] == '@';
    bool keep = !sent.empty() && sent[0] == '+';
    std::string path = data ? kDataRoot : kRoot;
    size_t start = data || keep ? 1 : 0;
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
    // a data file names a file under sdmc:/OpenShiina/<folder>, not the folder itself
    if (data)
        return path.size() > std::string(kDataRoot).size() + 1 ? path : std::string();
    return EndsWithNro(path) ? path : std::string();
}

struct Upload
{
    std::string path;
    /// A data file (a path with '@') or an .nro to keep (with '+'): saved, not started.
    bool data = false;
    std::vector<std::string> arguments;
};

/// The arguments as nxlink sends them, each ended by a zero byte.
std::vector<std::string> SplitArguments(const std::string& arguments)
{
    std::vector<std::string> list;
    for (size_t start = 0; start < arguments.size();)
    {
        size_t end = arguments.find('\0', start);
        if (end == std::string::npos)
            end = arguments.size();
        list.push_back(arguments.substr(start, end - start));
        start = end + 1;
    }
    return list;
}

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

/// A read ("GETF" already taken): the path's length and the path, then the file or the folder's
/// entries, as the comment at the top tells. Only the SD card is read, nothing is written.
bool Serve(int sock, std::string& path, std::string& error)
{
    int32_t length = 0;
    if (!ReceiveInt(sock, length) || length <= 0 || length > 1024)
    {
        error = "a read without a path";
        return false;
    }
    path.assign(size_t(length), '\0');
    if (!ReceiveAll(sock, path.data(), path.size()))
    {
        error = "a read without a path";
        return false;
    }
    if (path.rfind("sdmc:/", 0) != 0 || path.find("/..") != std::string::npos)
    {
        SendInt(sock, -1);
        error = "not a path of the SD card: " + path;
        return false;
    }
    struct stat info{};
    if (stat(path.c_str(), &info) != 0)
    {
        SendInt(sock, -1);
        error = "no " + path + ": " + std::strerror(errno);
        return false;
    }
    if (S_ISDIR(info.st_mode))
    {
        std::string listing;
        if (DIR* dir = opendir(path.c_str()))
        {
            std::string base = path.back() == '/' ? path : path + "/";
            while (dirent* entry = readdir(dir))
            {
                std::string name = entry->d_name;
                if (name == "." || name == "..")
                    continue;
                struct stat child{};
                bool isDir = stat((base + name).c_str(), &child) == 0 && S_ISDIR(child.st_mode);
                listing += std::string(isDir ? "D" : "F") + "\t" + std::to_string(isDir ? 0 : int64_t(child.st_size)) + "\t" + name + "\n";
            }
            closedir(dir);
        }
        if (!SendInt(sock, 1) || !SendInt(sock, int32_t(listing.size())) || !SendAll(sock, listing.data(), listing.size()))
        {
            error = "the connection stopped";
            return false;
        }
        return true;
    }
    std::FILE* file = Files::Open(path, "rb");
    if (!file)
    {
        SendInt(sock, -1);
        error = "cannot read " + path + ": " + std::strerror(errno);
        return false;
    }
    int64_t size = int64_t(info.st_size);
    bool ok = SendInt(sock, 0) && SendAll(sock, &size, sizeof size);
    std::vector<uint8_t> buffer(256 * 1024);
    int64_t sent = 0;
    while (ok && sent < size)
    {
        size_t got = std::fread(buffer.data(), 1, buffer.size(), file);
        if (got == 0)
            break;
        ok = SendAll(sock, buffer.data(), got);
        sent += int64_t(got);
    }
    std::fclose(file);
    if (!ok || sent != size)
    {
        error = "sent " + std::to_string(sent) + " bytes of " + std::to_string(size);
        return false;
    }
    return true;
}

/// A write ("PUTF" already taken): the path's length and the path, the size, then the bytes as they
/// are, as the comment at the top tells. bytes: how many arrived.
bool Put(int sock, std::string& path, int64_t& bytes, std::string& error)
{
    bytes = 0;
    int32_t length = 0;
    std::string name;
    int64_t size = -1;
    if (!ReceiveInt(sock, length) || length <= 0 || length > 1024)
    {
        error = "a write without a path";
        return false;
    }
    name.assign(size_t(length), '\0');
    if (!ReceiveAll(sock, name.data(), name.size()) || !ReceiveAll(sock, &size, sizeof size) || size < 0)
    {
        error = "a write without a path or a size";
        return false;
    }
    path = TargetPath("@" + name);
    std::string part = path + ".part";
    std::FILE* file = nullptr;
    if (path.empty())
        error = "only a file under sdmc:/OpenShiina is written: " + name;
    else if (!Files::CreateDirectories(Files::DirectoryName(path)) || !(file = Files::Open(part, "wb")))
        error = "cannot write " + part + ": " + std::strerror(errno);
    if (!SendInt(sock, file ? 0 : -1) || !file)
    {
        if (file)
        {
            std::fclose(file);
            unlink(part.c_str());
        }
        if (error.empty())
            error = "the connection stopped";
        return false;
    }
    Log("Receiving %s, %lld bytes\n", path.c_str(), (long long)size);
    // Blocks go round: the network fills one while a thread of its own writes the ones before to the
    // SD card, so neither waits for the other
    std::vector<std::vector<uint8_t>> blocks(kWriteBlocks, std::vector<uint8_t>(kWriteBlock));
    std::deque<int> empty, filled;
    std::vector<size_t> lengths(kWriteBlocks);
    for (int b = 0; b < kWriteBlocks; b++)
        empty.push_back(b);
    std::mutex lock;
    std::condition_variable changed;
    bool received = false, written = true;
    std::thread writer([&] {
        std::unique_lock held(lock);
        for (;;)
        {
            changed.wait(held, [&] { return !filled.empty() || received; });
            if (filled.empty())
                return;
            int b = filled.front();
            filled.pop_front();
            held.unlock();
            bool ok = std::fwrite(blocks[size_t(b)].data(), 1, lengths[size_t(b)], file) == lengths[size_t(b)];
            held.lock();
            written = written && ok;
            empty.push_back(b);
            changed.notify_all();
        }
    });
    bool ok = true;
    while (ok && bytes < size)
    {
        int b;
        {
            std::unique_lock held(lock);
            changed.wait(held, [&] { return !empty.empty() || !written; });
            if (!written)
                break;
            b = empty.front();
            empty.pop_front();
        }
        std::vector<uint8_t>& block = blocks[size_t(b)];
        size_t want = size_t(std::min<int64_t>(int64_t(block.size()), size - bytes)), have = 0;
        while (have < want)
        {
            ssize_t got = recv(sock, block.data() + have, want - have, 0);
            if (got <= 0)
                break;
            have += size_t(got);
        }
        if (have != want)
        {
            error = "the connection stopped";
            ok = false;
        }
        std::lock_guard held(lock);
        lengths[size_t(b)] = have;
        filled.push_back(b);
        changed.notify_all();
        bytes += int64_t(have);
    }
    {
        std::lock_guard held(lock);
        received = true;
        changed.notify_all();
    }
    writer.join();
    if (!written)
    {
        error = "cannot write the file";
        ok = false;
    }
    ok = std::fclose(file) == 0 && ok;
    if (ok)
    {
        unlink(path.c_str());
        if (rename(part.c_str(), path.c_str()) != 0)
        {
            error = "cannot move " + part + ": " + std::strerror(errno);
            ok = false;
        }
    }
    if (!ok)
        unlink(part.c_str());
    SendInt(sock, ok ? 0 : -1);
    return ok;
}

/// A deletion ("DELF" already taken): the path's length and the path, a file under sdmc:/OpenShiina.
bool Delete(int sock, std::string& path, std::string& error)
{
    int32_t length = 0;
    if (!ReceiveInt(sock, length) || length <= 0 || length > 1024)
    {
        error = "a deletion without a path";
        return false;
    }
    path.assign(size_t(length), '\0');
    if (!ReceiveAll(sock, path.data(), path.size()))
    {
        error = "a deletion without a path";
        return false;
    }
    std::string root = std::string(kDataRoot) + "/";
    struct stat info{};
    if (path.rfind(root, 0) != 0 || path.size() <= root.size() || path.find("/..") != std::string::npos)
        error = "only a file under " + root + " is deleted: " + path;
    else if (stat(path.c_str(), &info) != 0 || !S_ISREG(info.st_mode))
        error = "no file " + path;
    else if (unlink(path.c_str()) != 0)
        error = "cannot delete " + path + ": " + std::strerror(errno);
    SendInt(sock, error.empty() ? 0 : -1);
    return error.empty();
}

/// One upload as hbmenu's netloader takes it: the path's length and the path, the file's size, a
/// response (0, or -1 when the file cannot be made), the compressed file, a second response, then the
/// arguments, each ending with a 0. The file is written next to its place and moved there at the end.
/// (The path's length is already read: it told an upload from a read.)
bool Receive(int sock, int32_t nameLength, Upload& upload, std::string& error)
{
    if (nameLength <= 0 || nameLength > 1024)
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
    upload.data = !name.empty() && (name[0] == '@' || name[0] == '+');
    std::string part = upload.path + ".part";
    std::FILE* file = nullptr;
    if (upload.path.empty())
        error = "not an .nro under sdmc:/switch, nor a file under sdmc:/OpenShiina: " + name;
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
    upload.arguments = SplitArguments(arguments);
    return true;
}

std::string Quoted(const std::string& argument) { return "\"" + argument + "\""; }

/// A run ("RUNF" already taken): the .nro under sdmc:/switch and its arguments; answers 0 when it is
/// there, -1 when not.
bool ReceiveRun(int sock, Upload& run, std::string& error)
{
    int32_t length = 0;
    std::string name, arguments;
    if (!ReceiveInt(sock, length) || length <= 0 || length > 1024)
    {
        error = "no path";
        return false;
    }
    name.resize(size_t(length));
    int32_t argumentsLength = 0;
    if (!ReceiveAll(sock, name.data(), name.size()) || !ReceiveInt(sock, argumentsLength) || argumentsLength < 0 || argumentsLength > 4096)
    {
        error = "no arguments after the path";
        return false;
    }
    arguments.resize(size_t(argumentsLength));
    if (!ReceiveAll(sock, arguments.data(), arguments.size()))
    {
        error = "no arguments after the path";
        return false;
    }
    run.path = !name.empty() && (name[0] == '@' || name[0] == '+') ? std::string() : TargetPath(name);
    bool there = !run.path.empty() && Files::IsFile(run.path);
    if (!there)
        error = "no .nro " + name + " under sdmc:/switch";
    run.arguments = SplitArguments(arguments);
    return SendInt(sock, there ? 0 : -1) && there;
}

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

// libnx's defaults with TCP buffers that grow to 4 MB (from 64 KB / 256 KB): what a transfer over
// Wi-Fi can have in flight is what it gets a round trip
const SocketInitConfig kSockets = [] {
    SocketInitConfig config = *socketGetDefaultInitConfig();
    config.tcp_tx_buf_size = 0x40000;
    config.tcp_rx_buf_size = 0x100000;
    config.tcp_tx_buf_max_size = 0x400000;
    config.tcp_rx_buf_max_size = 0x400000;
    return config;
}();

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
    else if (!(network = R_SUCCEEDED(socketInitialize(&kSockets)) || R_SUCCEEDED(socketInitializeDefault())))
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
        int window = 0x400000;
        setsockopt(sock, SOL_SOCKET, SO_RCVBUF, &window, sizeof window);
        setsockopt(sock, SOL_SOCKET, SO_SNDBUF, &window, sizeof window);
        int32_t first = 0;
        std::string error;
        if (ReceiveInt(sock, first) && first == kGetMagic)
        {
            std::string path;
            bool served = Serve(sock, path, error);
            close(sock);
            if (served)
                Log("Sent %s to %s\n", path.c_str(), inet_ntoa(remote.sin_addr));
            else
                Log("From %s: %s\n", inet_ntoa(remote.sin_addr), error.c_str());
            continue;
        }
        if (first == kPutMagic)
        {
            std::string path;
            int64_t bytes = 0;
            uint64_t start = armGetSystemTick();
            bool put = Put(sock, path, bytes, error);
            close(sock);
            double seconds = double(armTicksToNs(armGetSystemTick() - start)) / 1e9;
            if (put)
                Log("Saved %s (%.1f MB/s)\n", path.c_str(), seconds > 0 ? double(bytes) / 1048576.0 / seconds : 0.0);
            else
                Log("From %s: %s\n", inet_ntoa(remote.sin_addr), error.c_str());
            continue;
        }
        if (first == kDeleteMagic)
        {
            std::string path;
            bool deleted = Delete(sock, path, error);
            close(sock);
            if (deleted)
                Log("Deleted %s\n", path.c_str());
            else
                Log("From %s: %s\n", inet_ntoa(remote.sin_addr), error.c_str());
            continue;
        }
        Upload upload;
        // (a run started from the PC without nxlink has no nxlink -s to send its output to)
        bool fromNxlink = first != kRunMagic;
        bool received = fromNxlink ? Receive(sock, first, upload, error) : ReceiveRun(sock, upload, error);
        close(sock);
        if (!received)
        {
            Log("From %s: %s\n", inet_ntoa(remote.sin_addr), error.c_str());
            continue;
        }
        if (upload.data)
        {
            Log("Saved %s\n", upload.path.c_str());
            continue;
        }
        std::string line = Quoted(upload.path);
        for (const auto& argument : upload.arguments)
            line += " " + Quoted(argument);
        char nxlink[17];
        std::snprintf(nxlink, sizeof nxlink, "%08" PRIx32 "_NXLINK_", uint32_t(remote.sin_addr.s_addr));
        line += " " + Quoted("_DEVHOST_=" + self);
        if (fromNxlink)
            line += " " + Quoted(nxlink);
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
