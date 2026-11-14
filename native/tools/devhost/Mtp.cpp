// devhost's MTP responder (see Mtp.h). The USB device is made with usb:ds as libnx's usbComms makes
// its own (5.0.0 and later: descriptors for full, high and super speed, a BOS), with one interface of
// the still-image class (6 / 1 / 1, the one Windows binds its MTP driver to), a bulk endpoint each way
// and an interrupt one for events (none are sent). The protocol is PTP's containers (USB Still Image
// Capture Device Definition): a command, maybe a data phase either way, a response.
//
// One storage, sdmc:/OpenShiina (the player's folder: games, saves, logs), its folders and files as
// objects; a handle is given to a path the first time it is listed and kept for the session. The
// operations are those Windows' MTP driver needs to browse, copy both ways and delete: device and
// storage info, object handles, info and properties (by GetObjectPropValue, the property list
// operation is not offered), GetObject, SendObjectInfo + SendObject, DeleteObject. A file goes through
// a ring of 4 MB blocks: the cable fills (or sends) them on the responder's thread while a thread of
// the file's reads (or writes) the SD card, so neither waits for the other.
#include "Mtp.h"

#include <switch.h>

#include <dirent.h>
#include <sys/stat.h>
#include <unistd.h>

#include <algorithm>
#include <condition_variable>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <deque>
#include <iterator>
#include <map>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace openshiina::devhost {

namespace {

constexpr const char* kRoot = "sdmc:/OpenShiina";
constexpr uint32_t kStorage = 0x00010001;
constexpr size_t kBlock = 4 << 20;   // a bulk transfer of a file's data
constexpr int kBlocks = 3;           // the ring between the cable and the SD card
constexpr size_t kSmall = 0x10000;   // commands, responses, small data phases

// Container types and codes (PTP / MTP)
enum : uint16_t
{
    Command = 1,
    Data = 2,
    Response = 3,
};
enum : uint16_t
{
    GetDeviceInfo = 0x1001,
    OpenSession = 0x1002,
    CloseSession = 0x1003,
    GetStorageIDs = 0x1004,
    GetStorageInfo = 0x1005,
    GetObjectHandles = 0x1007,
    GetObjectInfo = 0x1008,
    GetObject = 0x1009,
    DeleteObject = 0x100B,
    SendObjectInfo = 0x100C,
    SendObject = 0x100D,
    GetDevicePropDesc = 0x1014,
    GetDevicePropValue = 0x1015,
    GetObjectPropsSupported = 0x9801,
    GetObjectPropDesc = 0x9802,
    GetObjectPropValue = 0x9803,
};
enum : uint16_t
{
    Ok = 0x2001,
    GeneralError = 0x2002,
    OperationNotSupported = 0x2005,
    InvalidStorageId = 0x2008,
    InvalidObjectHandle = 0x2009,
    DevicePropNotSupported = 0x200A,
    StoreFull = 0x200C,
    AccessDenied = 0x200F,
    NoValidObjectInfo = 0x2015,
    InvalidParameter = 0x201D,
    ObjectPropNotSupported = 0xA80A,
};
enum : uint16_t
{
    FormatUndefined = 0x3000,
    FormatAssociation = 0x3001,
};
enum : uint16_t
{
    TypeUint16 = 0x0004,
    TypeUint32 = 0x0006,
    TypeUint64 = 0x0008,
    TypeUint128 = 0x000A,
    TypeString = 0xFFFF,
};
enum : uint16_t
{
    PropStorageId = 0xDC01,
    PropObjectFormat = 0xDC02,
    PropProtectionStatus = 0xDC03,
    PropObjectSize = 0xDC04,
    PropObjectFileName = 0xDC07,
    PropDateModified = 0xDC09,
    PropParentObject = 0xDC0B,
    PropPersistentId = 0xDC41,
    PropName = 0xDC44,
    DevicePropFriendlyName = 0xD402,
};

const uint16_t kOperations[] = {GetDeviceInfo,     OpenSession,        CloseSession,      GetStorageIDs,           GetStorageInfo,
                                GetObjectHandles,  GetObjectInfo,      GetObject,         DeleteObject,            SendObjectInfo,
                                SendObject,        GetDevicePropDesc,  GetDevicePropValue, GetObjectPropsSupported, GetObjectPropDesc,
                                GetObjectPropValue};
const uint16_t kObjectProps[] = {PropStorageId,    PropObjectFormat, PropProtectionStatus, PropObjectSize, PropObjectFileName,
                                 PropDateModified, PropParentObject, PropPersistentId,     PropName};

// ---- UTF-8 / UTF-16 for MTP's strings ----

std::u16string ToUtf16(const std::string& s)
{
    std::u16string out;
    for (size_t i = 0; i < s.size();)
    {
        uint32_t c = uint8_t(s[i]);
        int n = c < 0x80 ? 1 : c < 0xE0 ? 2 : c < 0xF0 ? 3 : 4;
        if (n > 1)
        {
            c &= 0xFF >> (n + 1);
            for (int k = 1; k < n && i + size_t(k) < s.size(); k++)
                c = c << 6 | (uint8_t(s[i + size_t(k)]) & 0x3F);
        }
        i += size_t(n);
        if (c >= 0x10000)
        {
            c -= 0x10000;
            out += char16_t(0xD800 + (c >> 10));
            out += char16_t(0xDC00 + (c & 0x3FF));
        }
        else
            out += char16_t(c);
    }
    return out;
}

std::string ToUtf8(const std::u16string& s)
{
    std::string out;
    for (size_t i = 0; i < s.size(); i++)
    {
        uint32_t c = s[i];
        if (c >= 0xD800 && c < 0xDC00 && i + 1 < s.size())
            c = 0x10000 + ((c - 0xD800) << 10) + (s[++i] - 0xDC00);
        if (c < 0x80)
            out += char(c);
        else if (c < 0x800)
            out += {char(0xC0 | c >> 6), char(0x80 | (c & 0x3F))};
        else if (c < 0x10000)
            out += {char(0xE0 | c >> 12), char(0x80 | ((c >> 6) & 0x3F)), char(0x80 | (c & 0x3F))};
        else
            out += {char(0xF0 | c >> 18), char(0x80 | ((c >> 12) & 0x3F)), char(0x80 | ((c >> 6) & 0x3F)), char(0x80 | (c & 0x3F))};
    }
    return out;
}

// ---- A data set being written ----

struct Writer
{
    std::vector<uint8_t> Bytes;
    void U8(uint8_t v) { Bytes.push_back(v); }
    void U16(uint16_t v) { Bytes.insert(Bytes.end(), {uint8_t(v), uint8_t(v >> 8)}); }
    void U32(uint32_t v)
    {
        U16(uint16_t(v));
        U16(uint16_t(v >> 16));
    }
    void U64(uint64_t v)
    {
        U32(uint32_t(v));
        U32(uint32_t(v >> 32));
    }
    void String(const std::string& s)
    {
        std::u16string w = ToUtf16(s);
        if (w.empty())
        {
            U8(0);
            return;
        }
        w = w.substr(0, 254);
        U8(uint8_t(w.size() + 1));
        for (char16_t c : w)
            U16(uint16_t(c));
        U16(0);
    }
    void Array16(const uint16_t* v, size_t n)
    {
        U32(uint32_t(n));
        for (size_t k = 0; k < n; k++)
            U16(v[k]);
    }
};

/// An MTP string at bytes[at] (at moves past it).
std::string ReadString(const uint8_t* bytes, size_t size, size_t& at)
{
    if (at >= size)
        return {};
    size_t n = bytes[at++];
    std::u16string w;
    for (size_t k = 0; k < n && at + 1 < size; k++, at += 2)
    {
        char16_t c = char16_t(bytes[at] | bytes[at + 1] << 8);
        if (c != 0)
            w += c;
    }
    return ToUtf8(w);
}

uint32_t Read32(const uint8_t* p) { return uint32_t(p[0]) | uint32_t(p[1]) << 8 | uint32_t(p[2]) << 16 | uint32_t(p[3]) << 24; }
uint16_t Read16(const uint8_t* p) { return uint16_t(p[0] | p[1] << 8); }

/// Blocks handed from a producer to a consumer in order: the producer fills an empty one, the
/// consumer takes the filled ones; either side can give up (Fail), the producer ends (End).
class Ring
{
public:
    explicit Ring(uint8_t* const* blocks) : m_blocks(blocks)
    {
        for (int b = 0; b < kBlocks; b++)
            m_empty.push_back(b);
    }
    uint8_t* Block(int b) const { return m_blocks[b]; }
    int TakeEmpty()
    {
        std::unique_lock held(m_lock);
        m_changed.wait(held, [&] { return !m_empty.empty() || m_failed; });
        if (m_failed)
            return -1;
        int b = m_empty.front();
        m_empty.pop_front();
        return b;
    }
    void PutFilled(int b, size_t offset, size_t length)
    {
        std::lock_guard held(m_lock);
        m_offset[b] = offset;
        m_length[b] = length;
        m_filled.push_back(b);
        m_changed.notify_all();
    }
    /// The next filled block (its offset and length), -1 once the producer ended (or failed).
    int TakeFilled(size_t& offset, size_t& length)
    {
        std::unique_lock held(m_lock);
        m_changed.wait(held, [&] { return !m_filled.empty() || m_ended || m_failed; });
        if (m_filled.empty() || m_failed)
            return -1;
        int b = m_filled.front();
        m_filled.pop_front();
        offset = m_offset[b];
        length = m_length[b];
        return b;
    }
    void PutEmpty(int b)
    {
        std::lock_guard held(m_lock);
        m_empty.push_back(b);
        m_changed.notify_all();
    }
    void End()
    {
        std::lock_guard held(m_lock);
        m_ended = true;
        m_changed.notify_all();
    }
    void Fail()
    {
        std::lock_guard held(m_lock);
        m_failed = true;
        m_changed.notify_all();
    }
    bool Failed()
    {
        std::lock_guard held(m_lock);
        return m_failed;
    }

private:
    uint8_t* const* m_blocks;
    std::deque<int> m_empty, m_filled;
    size_t m_offset[kBlocks] = {}, m_length[kBlocks] = {};
    bool m_ended = false, m_failed = false;
    std::mutex m_lock;
    std::condition_variable m_changed;
};

std::string Date(time_t t)
{
    std::tm parts{};
    gmtime_r(&t, &parts);
    char text[32];
    std::strftime(text, sizeof text, "%Y%m%dT%H%M%S", &parts);
    return text;
}

class Responder
{
public:
    Responder(std::function<void(const std::string&)> log, std::atomic<int>* busy) : m_log(std::move(log)), m_busy(busy) {}

    bool Setup();
    void Run();

private:
    // ---- USB ----
    bool Finish(UsbDsEndpoint* ep, uint32_t urb, size_t& done);
    bool Read(void* buffer, size_t size, size_t& done);
    bool Write(const void* buffer, size_t size, bool last);
    bool Configured()
    {
        UsbState state = UsbState_Detached;
        return R_SUCCEEDED(usbDsGetState(&state)) && state == UsbState_Configured;
    }

    // ---- containers ----
    bool SendData(uint16_t code, const std::vector<uint8_t>& payload);
    bool Respond(uint16_t code, std::initializer_list<uint32_t> params = {});
    /// The host's data phase into m_in (header taken off); false when the cable went.
    bool ReceiveData(std::vector<uint8_t>& payload);
    void Handle(const uint8_t* command, size_t size);

    // ---- objects ----
    struct Object
    {
        std::string Path;
        uint32_t Parent = 0;   // 0: in the storage's root
    };
    uint32_t HandleFor(const std::string& path, uint32_t parent);
    Object* Find(uint32_t handle)
    {
        auto found = m_objects.find(handle);
        return found == m_objects.end() ? nullptr : &found->second;
    }
    std::string FolderOf(uint32_t parent)
    {
        if (parent == 0 || parent == 0xFFFFFFFF)
            return kRoot;
        Object* o = Find(parent);
        return o ? o->Path : std::string();
    }
    void Forget(const std::string& path);
    static bool IsDirectory(const std::string& path)
    {
        struct stat info{};
        return stat(path.c_str(), &info) == 0 && S_ISDIR(info.st_mode);
    }
    static std::string NameOf(const std::string& path) { return path.substr(path.rfind('/') + 1); }
    bool PropValue(Writer& w, uint32_t handle, uint16_t prop);

    void GetFile(uint32_t handle);
    void PutFile();

    std::function<void(const std::string&)> m_log;
    std::atomic<int>* m_busy;
    UsbDsInterface* m_interface = nullptr;
    UsbDsEndpoint* m_in = nullptr;
    UsbDsEndpoint* m_out = nullptr;
    UsbDsEndpoint* m_events = nullptr;
    uint8_t* m_small = nullptr;
    uint8_t* m_blocks[kBlocks] = {};
    uint32_t m_transaction = 0;
    bool m_zlt = false;
    uint32_t m_packet = 512;

    std::map<uint32_t, Object> m_objects;
    std::map<std::string, uint32_t> m_handles;
    uint32_t m_next = 1;
    // SendObjectInfo's object, for the SendObject after it
    uint32_t m_pending = 0;
    uint64_t m_pendingSize = 0;
};

bool Responder::Setup()
{
    m_small = static_cast<uint8_t*>(aligned_alloc(0x1000, kSmall));
    for (auto& block : m_blocks)
        if (!(block = static_cast<uint8_t*>(aligned_alloc(0x1000, kBlock))))
            return false;
    if (!m_small || hosversionBefore(5, 0, 0) || R_FAILED(usbDsInitialize()))
        return false;
    static const u16 languages[] = {0x0409};
    u8 manufacturer = 0, product = 0, serial = 0, interfaceName = 0;
    if (R_FAILED(usbDsAddUsbLanguageStringDescriptor(nullptr, languages, 1)) || R_FAILED(usbDsAddUsbStringDescriptor(&manufacturer, "OpenShiina"))
        || R_FAILED(usbDsAddUsbStringDescriptor(&product, "OpenShiina devhost")) || R_FAILED(usbDsAddUsbStringDescriptor(&serial, "OPENSHIINA-DEVHOST"))
        || R_FAILED(usbDsAddUsbStringDescriptor(&interfaceName, "MTP")))
        return false;
    usb_device_descriptor device = {USB_DT_DEVICE_SIZE, USB_DT_DEVICE, 0x0110, 0, 0, 0, 0x40, 0x057E, 0x201D, 0x0100, manufacturer, product, serial, 1};
    if (R_FAILED(usbDsSetUsbDeviceDescriptor(UsbDeviceSpeed_Full, &device)))
        return false;
    device.bcdUSB = 0x0200;
    if (R_FAILED(usbDsSetUsbDeviceDescriptor(UsbDeviceSpeed_High, &device)))
        return false;
    device.bcdUSB = 0x0300;
    device.bMaxPacketSize0 = 0x09;
    if (R_FAILED(usbDsSetUsbDeviceDescriptor(UsbDeviceSpeed_Super, &device)))
        return false;
    static const u8 bos[0x16] = {0x05, USB_DT_BOS, 0x16, 0x00, 0x02, 0x07, USB_DT_DEVICE_CAPABILITY, 0x02, 0x02, 0x00, 0x00,
                                 0x00, 0x0A, USB_DT_DEVICE_CAPABILITY, 0x03, 0x00, 0x0E, 0x00, 0x03, 0x00, 0x00, 0x00};
    if (R_FAILED(usbDsSetBinaryObjectStore(bos, sizeof bos)) || R_FAILED(usbDsRegisterInterface(&m_interface)))
        return false;

    usb_interface_descriptor intf = {USB_DT_INTERFACE_SIZE, USB_DT_INTERFACE, m_interface->interface_index, 0, 3, USB_CLASS_IMAGE, 1, 1, interfaceName};
    usb_endpoint_descriptor in = {USB_DT_ENDPOINT_SIZE, USB_DT_ENDPOINT, uint8_t(USB_ENDPOINT_IN | (intf.bInterfaceNumber + 1)), USB_TRANSFER_TYPE_BULK, 0x40, 0};
    usb_endpoint_descriptor out = {USB_DT_ENDPOINT_SIZE, USB_DT_ENDPOINT, uint8_t(USB_ENDPOINT_OUT | (intf.bInterfaceNumber + 1)), USB_TRANSFER_TYPE_BULK, 0x40, 0};
    usb_endpoint_descriptor events = {USB_DT_ENDPOINT_SIZE, USB_DT_ENDPOINT, uint8_t(USB_ENDPOINT_IN | (intf.bInterfaceNumber + 2)), USB_TRANSFER_TYPE_INTERRUPT, 0x1C, 6};
    usb_ss_endpoint_companion_descriptor companion = {USB_DT_SS_ENDPOINT_COMPANION_SIZE, USB_DT_SS_ENDPOINT_COMPANION, 0x0F, 0x00, 0x00};
    usb_ss_endpoint_companion_descriptor eventCompanion = {USB_DT_SS_ENDPOINT_COMPANION_SIZE, USB_DT_SS_ENDPOINT_COMPANION, 0x00, 0x00, 0x1C};
    auto append = [&](UsbDeviceSpeed speed, const void* d, size_t n) { return R_SUCCEEDED(usbDsInterface_AppendConfigurationData(m_interface, speed, d, n)); };
    bool ok = append(UsbDeviceSpeed_Full, &intf, sizeof intf) && append(UsbDeviceSpeed_Full, &in, sizeof in) && append(UsbDeviceSpeed_Full, &out, sizeof out)
              && append(UsbDeviceSpeed_Full, &events, sizeof events);
    in.wMaxPacketSize = out.wMaxPacketSize = 0x200;
    ok = ok && append(UsbDeviceSpeed_High, &intf, sizeof intf) && append(UsbDeviceSpeed_High, &in, sizeof in)
         && append(UsbDeviceSpeed_High, &out, sizeof out) && append(UsbDeviceSpeed_High, &events, sizeof events);
    in.wMaxPacketSize = out.wMaxPacketSize = 0x400;
    ok = ok && append(UsbDeviceSpeed_Super, &intf, sizeof intf) && append(UsbDeviceSpeed_Super, &in, sizeof in)
         && append(UsbDeviceSpeed_Super, &companion, sizeof companion) && append(UsbDeviceSpeed_Super, &out, sizeof out)
         && append(UsbDeviceSpeed_Super, &companion, sizeof companion) && append(UsbDeviceSpeed_Super, &events, sizeof events)
         && append(UsbDeviceSpeed_Super, &eventCompanion, sizeof eventCompanion);
    ok = ok && R_SUCCEEDED(usbDsInterface_RegisterEndpoint(m_interface, &m_in, in.bEndpointAddress))
         && R_SUCCEEDED(usbDsInterface_RegisterEndpoint(m_interface, &m_out, out.bEndpointAddress))
         && R_SUCCEEDED(usbDsInterface_RegisterEndpoint(m_interface, &m_events, events.bEndpointAddress))
         && R_SUCCEEDED(usbDsInterface_EnableInterface(m_interface)) && R_SUCCEEDED(usbDsEnable());
    return ok;
}

bool Responder::Finish(UsbDsEndpoint* ep, uint32_t urb, size_t& done)
{
    // waited for in steps, so that a cable pulled out ends the transfer
    for (;;)
    {
        if (R_SUCCEEDED(eventWait(&ep->CompletionEvent, 1'000'000'000)))
            break;
        if (!Configured())
        {
            usbDsEndpoint_Cancel(ep);
            eventWait(&ep->CompletionEvent, 1'000'000'000);
            eventClear(&ep->CompletionEvent);
            return false;
        }
    }
    eventClear(&ep->CompletionEvent);
    UsbDsReportData report{};
    u32 transferred = 0;
    if (R_FAILED(usbDsEndpoint_GetReportData(ep, &report)) || R_FAILED(usbDsParseReportData(&report, urb, nullptr, &transferred)))
        return false;
    done = transferred;
    return true;
}

bool Responder::Read(void* buffer, size_t size, size_t& done)
{
    u32 urb = 0;
    return R_SUCCEEDED(usbDsEndpoint_PostBufferAsync(m_out, buffer, size, &urb)) && Finish(m_out, urb, done);
}

bool Responder::Write(const void* buffer, size_t size, bool last)
{
    // a zero-length packet after the last transfer of a data phase that ends on a packet's end (and
    // none between its transfers)
    if (m_zlt != last)
    {
        usbDsEndpoint_SetZlt(m_in, last);
        m_zlt = last;
    }
    u32 urb = 0;
    size_t done = 0;
    return R_SUCCEEDED(usbDsEndpoint_PostBufferAsync(m_in, const_cast<void*>(buffer), size, &urb)) && Finish(m_in, urb, done) && done == size;
}

bool Responder::SendData(uint16_t code, const std::vector<uint8_t>& payload)
{
    size_t total = 12 + payload.size();
    Writer header;
    header.U32(uint32_t(total));
    header.U16(Data);
    header.U16(code);
    header.U32(m_transaction);
    // in pieces of the small buffer (data sets are small; a folder of thousands of files is not)
    size_t at = 0;
    std::memcpy(m_small, header.Bytes.data(), 12);
    size_t fill = 12;
    while (true)
    {
        size_t n = std::min(payload.size() - at, kSmall - fill);
        std::memcpy(m_small + fill, payload.data() + at, n);
        at += n;
        fill += n;
        bool last = at == payload.size();
        if (!Write(m_small, fill, last))
            return false;
        if (last)
            return true;
        fill = 0;
    }
}

bool Responder::Respond(uint16_t code, std::initializer_list<uint32_t> params)
{
    Writer w;
    w.U32(uint32_t(12 + 4 * params.size()));
    w.U16(Response);
    w.U16(code);
    w.U32(m_transaction);
    for (uint32_t p : params)
        w.U32(p);
    std::memcpy(m_small, w.Bytes.data(), w.Bytes.size());
    return Write(m_small, w.Bytes.size(), true);
}

bool Responder::ReceiveData(std::vector<uint8_t>& payload)
{
    payload.clear();
    size_t done = 0;
    if (!Read(m_small, kSmall, done) || done < 12 || Read16(m_small + 4) != Data)
        return false;
    size_t total = Read32(m_small);
    payload.assign(m_small + 12, m_small + done);
    while (12 + payload.size() < total)
    {
        if (!Read(m_small, kSmall, done) || done == 0)
            return false;
        payload.insert(payload.end(), m_small, m_small + done);
    }
    return true;
}

uint32_t Responder::HandleFor(const std::string& path, uint32_t parent)
{
    auto found = m_handles.find(path);
    if (found != m_handles.end())
        return found->second;
    uint32_t handle = m_next++;
    m_handles[path] = handle;
    m_objects[handle] = {path, parent == 0xFFFFFFFF ? 0 : parent};
    return handle;
}

void Responder::Forget(const std::string& path)
{
    for (auto it = m_handles.begin(); it != m_handles.end();)
        if (it->first == path || it->first.rfind(path + "/", 0) == 0)
        {
            m_objects.erase(it->second);
            it = m_handles.erase(it);
        }
        else
            ++it;
}

bool Responder::PropValue(Writer& w, uint32_t handle, uint16_t prop)
{
    Object* o = Find(handle);
    if (!o)
        return false;
    struct stat info{};
    stat(o->Path.c_str(), &info);
    bool dir = S_ISDIR(info.st_mode);
    switch (prop)
    {
    case PropStorageId:
        w.U32(kStorage);
        return true;
    case PropObjectFormat:
        w.U16(dir ? FormatAssociation : FormatUndefined);
        return true;
    case PropProtectionStatus:
        w.U16(0);
        return true;
    case PropObjectSize:
        w.U64(dir ? 0 : uint64_t(info.st_size));
        return true;
    case PropObjectFileName:
    case PropName:
        w.String(NameOf(o->Path));
        return true;
    case PropDateModified:
        w.String(Date(info.st_mtime));
        return true;
    case PropParentObject:
        w.U32(o->Parent);
        return true;
    case PropPersistentId:
        w.U64(handle);
        w.U64(0);
        return true;
    default:
        return false;
    }
}

void Responder::GetFile(uint32_t handle)
{
    Object* o = Find(handle);
    struct stat info{};
    if (!o || stat(o->Path.c_str(), &info) != 0 || S_ISDIR(info.st_mode))
    {
        Respond(InvalidObjectHandle);
        return;
    }
    std::FILE* file = std::fopen(o->Path.c_str(), "rb");
    if (!file)
    {
        Respond(AccessDenied);
        return;
    }
    ++*m_busy;
    uint64_t size = uint64_t(info.st_size), total = size + 12;
    Writer header;
    header.U32(total > 0xFFFFFFFFull ? 0xFFFFFFFF : uint32_t(total));
    header.U16(Data);
    header.U16(GetObject);
    header.U32(m_transaction);
    // The SD card read ahead on a thread of its own, the first block after the container's header
    Ring ring(m_blocks);
    std::thread reader([&] {
        size_t first = 12;
        uint64_t read = 0;
        while (read < size)
        {
            int b = ring.TakeEmpty();
            if (b < 0)
                return;
            size_t n = std::fread(ring.Block(b) + first, 1, size_t(std::min<uint64_t>(kBlock - first, size - read)), file);
            if (n == 0)
            {
                ring.Fail();
                return;
            }
            if (first != 0)
                std::memcpy(ring.Block(b), header.Bytes.data(), 12);
            ring.PutFilled(b, 0, first + n);
            read += n;
            first = 0;
        }
        if (size == 0)
        {
            int b = ring.TakeEmpty();
            if (b < 0)
                return;
            std::memcpy(ring.Block(b), header.Bytes.data(), 12);
            ring.PutFilled(b, 0, 12);
        }
        ring.End();
    });
    uint64_t sent = 0;
    bool ok = true;
    size_t offset = 0, length = 0;
    for (int b; ok && (b = ring.TakeFilled(offset, length)) >= 0;)
    {
        sent += length;
        ok = Write(ring.Block(b) + offset, length, sent >= total);
        ring.PutEmpty(b);
    }
    if (!ok)
        ring.Fail();
    reader.join();
    std::fclose(file);
    --*m_busy;
    if (ok && sent == total)
        Respond(Ok);
}

void Responder::PutFile()
{
    Object* o = Find(m_pending);
    if (!o)
    {
        // the data phase is taken all the same
        std::vector<uint8_t> ignored;
        ReceiveData(ignored);
        Respond(NoValidObjectInfo);
        return;
    }
    std::string path = o->Path;
    std::FILE* file = std::fopen(path.c_str(), "wb");
    ++*m_busy;
    uint64_t start = armGetSystemTick();
    // The cable read into the ring on this thread, the SD card written from it on a thread of its own
    Ring ring(m_blocks);
    bool written = file != nullptr;
    std::thread writer([&] {
        size_t offset = 0, length = 0;
        for (int b; (b = ring.TakeFilled(offset, length)) >= 0;)
        {
            if (written && length > 0)
                written = std::fwrite(ring.Block(b) + offset, 1, length, file) == length;
            ring.PutEmpty(b);
        }
    });
    uint64_t total = 0, received = 0;
    bool ok = true;
    for (bool first = true; ok && (first || received < total); first = false)
    {
        int b = ring.TakeEmpty();
        size_t done = 0;
        ok = b >= 0 && Read(ring.Block(b), kBlock, done);
        if (ok && first)
        {
            ok = done >= 12 && Read16(ring.Block(b) + 4) == Data;
            total = ok ? Read32(ring.Block(b)) : 0;
            if (total == 0xFFFFFFFF)
                total = m_pendingSize + 12;
        }
        if (ok && !first && done == 0)
            ok = false;
        if (!ok)
            break;
        received += done;
        ring.PutFilled(b, first ? 12 : 0, first ? done - 12 : done);
    }
    if (ok)
        ring.End();
    else
        ring.Fail();
    writer.join();
    if (file)
        written = std::fclose(file) == 0 && written;
    --*m_busy;
    if (!ok || !written)
    {
        unlink(path.c_str());
        Forget(path);
        m_pending = 0;
        if (ok)
            Respond(StoreFull);
        m_log("MTP: " + NameOf(path) + " was not written whole");
        return;
    }
    double seconds = double(armTicksToNs(armGetSystemTick() - start)) / 1e9;
    char line[96];
    std::snprintf(line, sizeof line, " (%.1f MB/s)", seconds > 0 ? double(total - 12) / 1048576.0 / seconds : 0.0);
    m_log("MTP: saved " + path.substr(std::strlen(kRoot) + 1) + line);
    m_pending = 0;
    Respond(Ok);
}

void Responder::Handle(const uint8_t* command, size_t size)
{
    uint16_t code = Read16(command + 6);
    m_transaction = Read32(command + 8);
    uint32_t p[5] = {};
    for (size_t k = 0; k < 5 && 12 + 4 * k + 4 <= size; k++)
        p[k] = Read32(command + 12 + 4 * k);
    Writer w;
    switch (code)
    {
    case GetDeviceInfo:
    {
        w.U16(100);
        w.U32(6);   // Microsoft's extensions
        w.U16(100);
        w.String("microsoft.com: 1.0; ");
        w.U16(0);
        w.Array16(kOperations, std::size(kOperations));
        w.Array16(nullptr, 0);
        const uint16_t deviceProps[] = {DevicePropFriendlyName};
        w.Array16(deviceProps, 1);
        w.Array16(nullptr, 0);
        const uint16_t formats[] = {FormatUndefined, FormatAssociation};
        w.Array16(formats, 2);
        w.String("OpenShiina");
        w.String("devhost");
        w.String("1.0");
        w.String("OPENSHIINA-DEVHOST");
        SendData(code, w.Bytes) && Respond(Ok);
        return;
    }
    case OpenSession:
        m_objects.clear();
        m_handles.clear();
        m_next = 1;
        Respond(Ok);
        return;
    case CloseSession:
        Respond(Ok);
        return;
    case GetStorageIDs:
        w.U32(1);
        w.U32(kStorage);
        SendData(code, w.Bytes) && Respond(Ok);
        return;
    case GetStorageInfo:
    {
        if (p[0] != kStorage)
        {
            Respond(InvalidStorageId);
            return;
        }
        s64 total = 0, free = 0;
        if (FsFileSystem* fs = fsdevGetDeviceFileSystem("sdmc"))
        {
            fsFsGetTotalSpace(fs, "/", &total);
            fsFsGetFreeSpace(fs, "/", &free);
        }
        w.U16(0x0004);   // removable RAM
        w.U16(0x0002);   // generic hierarchical
        w.U16(0x0000);   // read and write
        w.U64(uint64_t(total));
        w.U64(uint64_t(free));
        w.U32(0xFFFFFFFF);
        w.String("SD card: OpenShiina");
        w.String("");
        SendData(code, w.Bytes) && Respond(Ok);
        return;
    }
    case GetObjectHandles:
    {
        if (p[0] != kStorage && p[0] != 0xFFFFFFFF)
        {
            Respond(InvalidStorageId);
            return;
        }
        std::string folder = FolderOf(p[2]);
        if (folder.empty() || !IsDirectory(folder))
        {
            Respond(InvalidObjectHandle);
            return;
        }
        std::vector<uint32_t> handles;
        if (DIR* dir = opendir(folder.c_str()))
        {
            while (dirent* entry = readdir(dir))
            {
                std::string name = entry->d_name;
                if (name != "." && name != "..")
                    handles.push_back(HandleFor(folder + "/" + name, p[2] == 0xFFFFFFFF ? 0 : p[2]));
            }
            closedir(dir);
        }
        w.U32(uint32_t(handles.size()));
        for (uint32_t h : handles)
            w.U32(h);
        SendData(code, w.Bytes) && Respond(Ok);
        return;
    }
    case GetObjectInfo:
    {
        Object* o = Find(p[0]);
        struct stat info{};
        if (!o || stat(o->Path.c_str(), &info) != 0)
        {
            Respond(InvalidObjectHandle);
            return;
        }
        bool dir = S_ISDIR(info.st_mode);
        w.U32(kStorage);
        w.U16(dir ? FormatAssociation : FormatUndefined);
        w.U16(0);
        w.U32(dir ? 0 : uint32_t(std::min<int64_t>(info.st_size, 0xFFFFFFFF)));
        w.U16(0);
        for (int k = 0; k < 6; k++)
            w.U32(0);
        w.U32(o->Parent);
        w.U16(dir ? 0x0001 : 0x0000);
        w.U32(0);
        w.U32(0);
        w.String(NameOf(o->Path));
        w.String(Date(info.st_mtime));
        w.String(Date(info.st_mtime));
        w.String("");
        SendData(code, w.Bytes) && Respond(Ok);
        return;
    }
    case GetObject:
        GetFile(p[0]);
        return;
    case DeleteObject:
    {
        Object* o = Find(p[0]);
        if (!o)
        {
            Respond(InvalidObjectHandle);
            return;
        }
        std::string path = o->Path;
        // a folder with what is in it, depth first
        std::vector<std::string> stack{path}, order;
        while (!stack.empty())
        {
            std::string at = stack.back();
            stack.pop_back();
            order.push_back(at);
            if (IsDirectory(at))
                if (DIR* dir = opendir(at.c_str()))
                {
                    while (dirent* entry = readdir(dir))
                    {
                        std::string name = entry->d_name;
                        if (name != "." && name != "..")
                            stack.push_back(at + "/" + name);
                    }
                    closedir(dir);
                }
        }
        bool ok = true;
        for (auto it = order.rbegin(); it != order.rend(); ++it)
            ok = (IsDirectory(*it) ? rmdir(it->c_str()) : unlink(it->c_str())) == 0 && ok;
        Forget(path);
        m_log("MTP: deleted " + path.substr(std::strlen(kRoot) + 1));
        Respond(ok ? Ok : GeneralError);
        return;
    }
    case SendObjectInfo:
    {
        std::vector<uint8_t> info;
        if (!ReceiveData(info))
            return;
        std::string folder = FolderOf(p[1]);
        if (info.size() < 53 || folder.empty() || !IsDirectory(folder))
        {
            Respond(InvalidParameter);
            return;
        }
        uint16_t format = Read16(info.data() + 4);
        size_t at = 52;
        std::string name = ReadString(info.data(), info.size(), at);
        if (name.empty() || name == "." || name == ".." || name.find('/') != std::string::npos || name.find('\\') != std::string::npos)
        {
            Respond(InvalidParameter);
            return;
        }
        std::string path = folder + "/" + name;
        uint32_t parent = p[1] == 0xFFFFFFFF ? 0 : p[1];
        if (format == FormatAssociation)
        {
            mkdir(path.c_str(), 0777);
            uint32_t handle = HandleFor(path, parent);
            Respond(IsDirectory(path) ? Ok : AccessDenied, {kStorage, p[1], handle});
            return;
        }
        m_pending = HandleFor(path, parent);
        m_pendingSize = Read32(info.data() + 8);
        Respond(Ok, {kStorage, p[1], m_pending});
        return;
    }
    case SendObject:
        PutFile();
        return;
    case GetDevicePropDesc:
        if (p[0] != DevicePropFriendlyName)
        {
            Respond(DevicePropNotSupported);
            return;
        }
        w.U16(DevicePropFriendlyName);
        w.U16(TypeString);
        w.U8(0);
        w.String("OpenShiina devhost");
        w.String("OpenShiina devhost");
        w.U8(0);
        SendData(code, w.Bytes) && Respond(Ok);
        return;
    case GetDevicePropValue:
        if (p[0] != DevicePropFriendlyName)
        {
            Respond(DevicePropNotSupported);
            return;
        }
        w.String("OpenShiina devhost");
        SendData(code, w.Bytes) && Respond(Ok);
        return;
    case GetObjectPropsSupported:
        w.Array16(kObjectProps, std::size(kObjectProps));
        SendData(code, w.Bytes) && Respond(Ok);
        return;
    case GetObjectPropDesc:
    {
        uint16_t prop = uint16_t(p[0]);
        uint16_t type = prop == PropStorageId || prop == PropParentObject ? TypeUint32
                        : prop == PropObjectFormat || prop == PropProtectionStatus ? TypeUint16
                        : prop == PropObjectSize                                    ? TypeUint64
                        : prop == PropPersistentId                                  ? TypeUint128
                                                                                    : TypeString;
        if (std::find(std::begin(kObjectProps), std::end(kObjectProps), prop) == std::end(kObjectProps))
        {
            Respond(ObjectPropNotSupported);
            return;
        }
        w.U16(prop);
        w.U16(type);
        w.U8(0);
        switch (type)
        {
        case TypeUint16:
            w.U16(0);
            break;
        case TypeUint32:
            w.U32(0);
            break;
        case TypeUint64:
            w.U64(0);
            break;
        case TypeUint128:
            w.U64(0);
            w.U64(0);
            break;
        default:
            w.String("");
            break;
        }
        w.U32(0);
        w.U8(0);
        SendData(code, w.Bytes) && Respond(Ok);
        return;
    }
    case GetObjectPropValue:
        if (!Find(p[0]))
        {
            Respond(InvalidObjectHandle);
            return;
        }
        if (!PropValue(w, p[0], uint16_t(p[1])))
        {
            Respond(ObjectPropNotSupported);
            return;
        }
        SendData(code, w.Bytes) && Respond(Ok);
        return;
    default:
        Respond(OperationNotSupported);
        return;
    }
}

void Responder::Run()
{
    for (;;)
    {
        if (R_FAILED(usbDsWaitReady(UINT64_MAX)))
        {
            svcSleepThread(1'000'000'000);
            continue;
        }
        UsbDeviceSpeed speed = UsbDeviceSpeed_High;
        usbDsGetSpeed(&speed);
        m_packet = speed == UsbDeviceSpeed_Super ? 1024 : speed == UsbDeviceSpeed_High ? 512 : 64;
        m_log("MTP: connected over USB");
        while (Configured())
        {
            size_t done = 0;
            if (!Read(m_small, kSmall, done))
                break;
            // (a zero-length packet left from a data phase, or anything not a command: passed over)
            if (done < 12 || Read16(m_small + 4) != Command)
                continue;
            std::vector<uint8_t> command(m_small, m_small + done);
            Handle(command.data(), command.size());
        }
        m_log("MTP: the cable went");
        m_pending = 0;
    }
}

void ThreadMain(void* argument) { static_cast<Responder*>(argument)->Run(); }

} // namespace

bool StartMtp(std::function<void(const std::string&)> log, std::atomic<int>* busy)
{
    // (never freed: the responder runs until devhost ends)
    auto* responder = new Responder(log, busy);
    if (!responder->Setup())
    {
        log("MTP: the USB device could not be set up");
        return false;
    }
    static Thread thread;
    if (R_FAILED(threadCreate(&thread, ThreadMain, responder, nullptr, 0x40000, 0x2C, -2)) || R_FAILED(threadStart(&thread)))
    {
        log("MTP: no thread for it");
        return false;
    }
    log("MTP: files over USB too (sdmc:/OpenShiina shows in Windows' Explorer)");
    return true;
}

} // namespace openshiina::devhost
