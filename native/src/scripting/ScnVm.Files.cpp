// C# Scripting/ScnVm.Files.cs: settings files and loose files of the game folder - the engine's
// GetPrivateProfile* calls on RIO.INI (op_0104 chooses the file, op_0107 reads a number), read
// here from the bytes the host gives for a loose file - and the files the scripts open.
//
// Not here: "Play from here" (a Tools feature). The files a scenario names are read ahead on other
// threads (ScnVm.ReadAhead), which changes only when a file is read, never its bytes.
#include "scripting/ScnVm.h"

#include <algorithm>

#include "text/Text.h"

namespace openshiina::scripting {

namespace {

const std::string TranslationFolder = "translation\\";

std::string Upper(const std::string& text) { return SjisFold(text); }

bool EndsWithTxt(const std::string& name) { return SjisEndsWithIgnoreCase(name, ".txt"); }

/// Where the last '\' or '/' is, as LastIndexOfAny(['\\', '/']) in the decoded text.
size_t LastSeparator(const std::string& path)
{
    size_t a = SjisFindLast(path, '\\'), b = SjisFindLast(path, '/');
    if (a == std::string::npos)
        return b;
    if (b == std::string::npos)
        return a;
    return std::max(a, b);
}

std::string Slashes(std::string path)
{
    for (size_t at = 0; at < path.size(); at++)
    {
        uint8_t b = uint8_t(path[at]);
        if ((b >= 0x81 && b <= 0x9F) || (b >= 0xE0 && b <= 0xFC))
            at++;
        else if (path[at] == '/')
            path[at] = '\\';
    }
    return path;
}

std::string TrimEndSeparators(std::string path)
{
    while (!path.empty() && (path.back() == '\\' || path.back() == '/'))
    {
        // a trail byte is never a separator: '\' (5C) can be one, so look at the character before
        size_t last = LastSeparator(path);
        if (last != path.size() - 1)
            break;
        path.pop_back();
    }
    return path;
}

} // namespace

int ScnVm::IniInt(const std::string& file, const std::string& section, const std::string& key, int fallback)
{
    auto value = IniString(file, section, key);
    if (!value)
        return fallback;
    std::string text = SjisTrim(*value);
    size_t at = 0;
    bool negative = false;
    if (at < text.size() && (text[at] == '-' || text[at] == '+'))
        negative = text[at++] == '-';
    int64_t number = 0;
    while (at < text.size() && text[at] >= '0' && text[at] <= '9')
        number = int64_t(uint64_t(number) * 10 + uint64_t(text[at++] - '0'));
    return int(negative ? -number : number);
}

std::optional<std::string> ScnVm::IniString(const std::string& file, const std::string& section, const std::string& key)
{
    if (auto written = m_iniWritten.find({Upper(file), Upper(section), Upper(key)}); written != m_iniWritten.end())
        return written->second;
    openshiina::Bytes bytes;
    if (!m_host.ReadLooseFile(file, bytes))
        return std::nullopt;
    std::string text(bytes.begin(), bytes.end());
    std::optional<std::string> current;
    size_t start = 0;
    while (start <= text.size())
    {
        size_t end = text.find('\n', start);
        std::string line = SjisTrim(text.substr(start, end == std::string::npos ? std::string::npos : end - start));
        start = end == std::string::npos ? text.size() + 1 : end + 1;
        if (!line.empty() && line.front() == '[' && line.back() == ']')
        {
            current = line.substr(1, line.size() - 2);
            continue;
        }
        size_t equals = SjisFind(line, '=');
        if (equals != std::string::npos && equals > 0 && current && SjisEqualsIgnoreCase(*current, section)
            && SjisEqualsIgnoreCase(SjisTrim(line.substr(0, equals)), key))
            return line.substr(equals + 1);
    }
    return std::nullopt;
}

void ScnVm::FolderMade(const std::string& path)
{
    std::string folder = TrimEndSeparators(Slashes(path));
    if (folder.empty() || SjisFind(folder, ':') != std::string::npos || folder.front() == '\\')
        return;
    // no ".." among its parts
    size_t start = 0;
    while (start <= folder.size())
    {
        size_t end = SjisFind(folder, '\\', start);
        if (folder.substr(start, end == std::string::npos ? std::string::npos : end - start) == "..")
            return;
        if (end == std::string::npos)
            break;
        start = end + 1;
    }
    for (const auto& made : m_madeFolders)
        if (SjisEqualsIgnoreCase(made, folder))
            return;
    m_madeFolders.push_back(folder);
}

bool ScnVm::InMadeFolder(const std::string& path, bool folderItself)
{
    std::string p = Slashes(path);
    for (const auto& folder : m_madeFolders)
        if (folderItself ? SjisEqualsIgnoreCase(TrimEndSeparators(p), folder) : SjisStartsWithIgnoreCase(p, folder + "\\"))
            return true;
    return false;
}

std::optional<ScnVm::ResolvedPath> ScnVm::ResolvePath(const std::string& path)
{
    std::string data = DataPath();
    size_t separator = LastSeparator(path);
    std::string name = separator == std::string::npos ? path : path.substr(separator + 1);
    if (SjisStartsWithIgnoreCase(path, data) || InMadeFolder(path, false))
        return ResolvedPath{true, name};
    // A full path elsewhere: not found, but for a file of the archives by its name (the engine
    // finds archive files by name alone)
    if (SjisFind(path, ':') != std::string::npos)
    {
        if (!name.empty() && m_host.ArchiveFileSize(name))
            return ResolvedPath{false, name};
        return std::nullopt;
    }
    return ResolvedPath{false, path};
}

const openshiina::Bytes* ScnVm::ModuleFile(const std::string& name)
{
    // The engine's own executable when the scripts read it: the copy the game keeps in its
    // archives, when it has one (START compares the two)
    if (!m_moduleFile || !SjisEqualsIgnoreCase(name, *m_moduleFile))
        return nullptr;
    if (!m_moduleCopyLooked)
    {
        m_moduleCopyLooked = true;
        m_moduleCopyFound = m_host.ExecutableCopy(*m_moduleFile, m_moduleCopy);
    }
    return m_moduleCopyFound ? &m_moduleCopy : nullptr;
}

bool ScnVm::ReadScriptFile(const std::string& path, openshiina::Bytes& data, bool)
{
    auto p = ResolvePath(path);
    if (!p)
        return false;
    if (p->Save)
        return ReadSave(p->Name, data);
    if (const auto* module = ModuleFile(p->Name))
    {
        data = *module;
        return true;
    }
    if (auto kept = m_fileCache.find(Upper(p->Name)); kept != m_fileCache.end())
    {
        m_fileCacheOrder.splice(m_fileCacheOrder.begin(), m_fileCacheOrder, kept->second);
        data = kept->second->second;
        ReadAheadAfter(p->Name);
        if (EndsWithTxt(p->Name))
            LearnScenario(p->Name, data);
        return true;
    }
    if (!TakeAhead(p->Name, data) && !ReadArchiveFile(p->Name, data))
        return m_host.ReadLooseFile(p->Name, data);
    ReadAheadAfter(p->Name);
    Kept(p->Name, data);
    if (EndsWithTxt(p->Name))
        LearnScenario(p->Name, data);
    return true;
}

// A file of the game folder's translation\ or of the archives as it is (the reader threads read
// them too: the archives and the folder listings are read under locks)
bool ScnVm::ReadArchiveFile(const std::string& name, openshiina::Bytes& data)
{
    if (m_host.ReadLooseFile(TranslationFolder + name, data))
        return true;
    if (auto plain = TranslationByFileName(name); plain && m_host.ReadLooseFile(*plain, data))
        return true;
    return m_host.ReadFile(name, data);
}

std::optional<std::string> ScnVm::TranslationByFileName(const std::string& name)
{
    size_t at = LastSeparator(name);
    if (at == std::string::npos)
        return std::nullopt;
    return TranslationFolder + name.substr(at + 1);
}

std::optional<int64_t> ScnVm::TranslationFileSize(const std::string& name)
{
    if (auto size = m_host.LooseFileSize(TranslationFolder + name))
        return size;
    if (auto plain = TranslationByFileName(name))
        return m_host.LooseFileSize(*plain);
    return std::nullopt;
}

void ScnVm::ForgetKept()
{
    m_fileCache.clear();
    m_fileCacheOrder.clear();
    m_fileCacheSize = 0;
}

void ScnVm::MountArchive(const std::string& archive, bool mounted)
{
    // A file in several archives comes from a mounted one, so what was kept may now be another
    // archive's
    if (!m_host.MountArchive(archive, mounted))
        return;
    ForgetKept();
    m_ahead.clear();
}

game::TranslationTables* ScnVm::Tables()
{
    if (!m_translationTablesRead)
    {
        m_translationTablesRead = true;
        auto tables = game::TranslationTables::Read([this](const std::string& file) -> std::optional<openshiina::Bytes> {
            openshiina::Bytes data;
            if (!m_host.ReadLooseFile(TranslationFolder + file, data))
                return std::nullopt;
            return data;
        });
        if (tables.Lines.size() + tables.Names.size() + tables.Strings.size() > 0)
            m_translationTables = std::move(tables);
    }
    return m_translationTables ? &*m_translationTables : nullptr;
}

void ScnVm::Translate(const std::string& name, openshiina::Bytes& data)
{
    // A file of the archives as the scripts read it: a scenario translated by text.tsv, a translated
    // scenario in Shift-JIS and gaiji
    if (!EndsWithTxt(name))
        return;
    if (!ScnGaiji::IsUtf8(data))
        if (auto* tables = Tables())
        {
            size_t at = LastSeparator(name);
            if (auto translated = tables->Apply(at == std::string::npos ? name : name.substr(at + 1), data))
                data = std::move(*translated);
        }
    if (ScnGaiji::IsUtf8(data))
    {
        data = m_gaiji.Scenario(data);
        if (IScnFonts* fonts = m_host.Fonts())
            fonts->Gaiji = &m_gaiji.Texts();
    }
}

void ScnVm::Kept(const std::string& name, openshiina::Bytes& data)
{
    // A file of the archives read: translated (Translate), then kept
    Translate(name, data);
    std::string key = Upper(name);
    if (int64_t(data.size()) > FileCacheBytes / 2 || m_fileCache.count(key))
        return;
    m_fileCacheOrder.emplace_front(key, data);
    m_fileCache[key] = m_fileCacheOrder.begin();
    m_fileCacheSize += int64_t(data.size());
    while (m_fileCacheSize > FileCacheBytes && !m_fileCacheOrder.empty())
    {
        auto& oldest = m_fileCacheOrder.back();
        m_fileCacheSize -= int64_t(oldest.second.size());
        m_fileCache.erase(oldest.first);
        m_fileCacheOrder.pop_back();
    }
}

std::optional<int64_t> ScnVm::ScriptFileSize(const std::string& path)
{
    auto p = ResolvePath(path);
    if (!p)
        return std::nullopt;
    openshiina::Bytes data;
    if (p->Save)
        return ReadSave(p->Name, data) ? std::optional<int64_t>(int64_t(data.size())) : std::nullopt;
    if (const auto* module = ModuleFile(p->Name))
        return int64_t(module->size());
    if (EndsWithTxt(p->Name) || TranslationFileSize(p->Name))
        return ReadScriptFile(path, data, false) ? std::optional<int64_t>(int64_t(data.size())) : std::nullopt;
    if (auto size = m_host.ArchiveFileSize(p->Name))
        return size;
    return m_host.LooseFileSize(p->Name);
}

bool ScnVm::WriteScriptFile(const std::string& path, const openshiina::Bytes& data)
{
    if (!FileWritesEnabled)
        return true;    // the engine reports success and writes nothing
    auto p = ResolvePath(path);
    if (!p || !p->Save)
        return false;
    WriteSave(p->Name, data);
    return true;
}

bool ScnVm::IsSaveFolder(const std::string& path)
{
    // The save folder (DataPath) or a folder in it; a name with a '.' is a file
    if (InMadeFolder(path, true))
        return true;
    std::string folder = TrimEndSeparators(DataPath());
    if (!SjisStartsWithIgnoreCase(path, folder) || path.size() < folder.size())
        return false;
    std::string rest = TrimEndSeparators(path.substr(folder.size()));
    if (rest.empty())
        return true;
    if (path[folder.size()] != '\\' && path[folder.size()] != '/')
        return false;
    size_t last = LastSeparator(rest);
    return SjisFind(last == std::string::npos ? rest : rest.substr(last + 1), '.') == std::string::npos;
}

void ScnVm::RegisterFiles()
{
    // 010E file, v: whether the file exists - for a file of the archives from their index
    Register(0x010E, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string path = vm.ReadString(vm.Value(c, i.Args[0]));
        vm.Store(c, i.Args[1], vm.ScriptFileSize(path) || vm.IsSaveFolder(path) ? 1 : 0);
        return 0;
    });
    // 0158 file, size, packed size: from the archive's index, as the engine does
    Register(0x0158, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string path = vm.ReadString(vm.Value(c, i.Args[0]));
        std::optional<int64_t> size;
        if (auto disc = vm.VirtualDiscSize(c, i, path))
            size = *disc;
        else
            size = vm.ScriptFileSize(path);
        if (!size)
            return 2;
        vm.Store(c, i.Args[1], int(*size));
        vm.Store(c, i.Args[2], int(*size));
        return 0;
    });
    // 0154 file, access, offset, size, packed size, handle: open for reading
    Register(0x0154, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string path = vm.ReadString(vm.Value(c, i.Args[0]));
        vm.Value(c, i.Args[1]);
        OpenFile file;
        if (!vm.ReadScriptFile(path, file.Data))
            return 2;
        int handle = vm.m_nextHandle++;
        int size = int(file.Data.size());
        vm.m_files[handle] = std::move(file);
        vm.Store(c, i.Args[2], 0);
        vm.Store(c, i.Args[3], size);
        vm.Store(c, i.Args[4], size);
        vm.Store(c, i.Args[5], handle);
        return 0;
    });
    // 0156 handle, buffer, n, v: ReadFile; v = the bytes read
    Register(0x0156, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int handle = vm.Value(c, i.Args[0]), buffer = vm.Value(c, i.Args[1]), n = vm.Value(c, i.Args[2]);
        auto found = vm.m_files.find(handle);
        if (found == vm.m_files.end())
            return 2;
        auto& file = found->second;
        int count = std::clamp(n, 0, int(file.Data.size()) - file.Position);
        vm.WriteBytes(buffer, file.Data.data() + file.Position, count);
        file.Position += count;
        vm.Store(c, i.Args[3], count);
        return 0;
    });
    // 0155 handle: CloseHandle
    Register(0x0155, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.m_files.erase(vm.Value(c, i.Args[0])); return 0; });
    // 0123 folder, file: DeleteFile (a damaged save file)
    Register(0x0123, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string path = vm.ReadString(vm.Value(c, i.Args[0])) + vm.ReadString(vm.Value(c, i.Args[1]));
        if (auto p = vm.ResolvePath(path); p && p->Save && vm.FileWritesEnabled)
            vm.DeleteSave(p->Name);
        return 0;
    });
    // 00D2 buffer, n, file: write a file
    Register(0x00D2, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int buffer = vm.Value(c, i.Args[0]), n = vm.Value(c, i.Args[1]);
        std::string path = vm.ReadString(vm.Value(c, i.Args[2]));
        return vm.WriteScriptFile(path, vm.ReadBytes(buffer, std::max(0, n))) ? 0 : 2;
    });

    // 0104 file: the settings file for 0107
    Register(0x0104, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.m_iniFile = vm.ReadString(vm.Value(c, i.Args[0])); return 0; });
    // 0107 section, key, v: v = the number in the settings file, v's own value when missing
    Register(0x0107, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string section = vm.ReadString(vm.Value(c, i.Args[0]));
        std::string key = vm.ReadString(vm.Value(c, i.Args[1]));
        int fallback = vm.Value(c, i.Args[2]);
        vm.Store(c, i.Args[2], vm.IniInt(vm.m_iniFile, section, key, fallback));
        return 0;
    });
    // 0105 section, key, v: v written as a number into the settings file; 0107 reads it back
    Register(0x0105, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string section = vm.ReadString(vm.Value(c, i.Args[0]));
        std::string key = vm.ReadString(vm.Value(c, i.Args[1]));
        vm.m_iniWritten[{Upper(vm.m_iniFile), Upper(section), Upper(key)}] = std::to_string(vm.Value(c, i.Args[2]));
        return 0;
    });
    // 0108 section, key, buffer, size: the text in the settings file, up to size - 1 bytes and a 0,
    // empty when missing (GetPrivateProfileStringA)
    Register(0x0108, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string section = vm.ReadString(vm.Value(c, i.Args[0]));
        std::string key = vm.ReadString(vm.Value(c, i.Args[1]));
        int buffer = vm.Value(c, i.Args[2]), size = vm.Value(c, i.Args[3]);
        if (buffer == 0 || size <= 0)
            return 0;
        std::string text = vm.IniString(vm.m_iniFile, section, key).value_or("");
        int n = std::min(int(text.size()), size - 1);
        vm.WriteBytes(buffer, reinterpret_cast<const uint8_t*>(text.data()), n);
        vm.WriteByte(buffer + n, 0);
        return 0;
    });

    // 0BB8 name, mode, v: fopen, only for the engine's debug output. Nothing is written into the
    // game's folder: v = 0, as a file that does not open; 0BB9 fclose, 0BC0 fputs and 0BC4 fwrite
    // (v = 0 items) do nothing with it
    Register(0x0BB8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        vm.Value(c, i.Args[1]);
        vm.Store(c, i.Args[2], 0);
        return 0;
    });
    Register(0x0BB9, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Value(c, i.Args[0]); return 0; });
    Register(0x0BC0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        vm.Value(c, i.Args[1]);
        return 0;
    });
    Register(0x0BC4, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        for (size_t k = 0; k < 4; k++)
            vm.Value(c, i.Args[k]);
        vm.Store(c, i.Args[4], 0);
        return 0;
    });
}

} // namespace openshiina::scripting
