// A test run of the Switch player (see TestRun.h)
#include "TestRun.h"

#include <algorithm>
#include <cstdio>
#include <format>

#include "miniz.h"
#include "platform/Files.h"

namespace openshiina::player {

namespace {

std::string Trim(const std::string& text)
{
    size_t a = text.find_first_not_of(" \t\r"), b = text.find_last_not_of(" \t\r");
    return a == std::string::npos ? std::string() : text.substr(a, b - a + 1);
}

std::vector<std::string> Items(const std::string& text, char separator)
{
    std::vector<std::string> items;
    for (size_t start = 0; start <= text.size();)
    {
        size_t end = text.find(separator, start);
        if (end == std::string::npos)
            end = text.size();
        std::string item = Trim(text.substr(start, end - start));
        if (!item.empty())
            items.push_back(item);
        start = end + 1;
    }
    return items;
}

int Number(const std::string& text) { return text.rfind("0x", 0) == 0 || text.rfind("0X", 0) == 0 ? std::stoi(text.substr(2), nullptr, 16) : std::stoi(text); }

} // namespace

std::optional<TestRun> TestRun::Read(const std::string& root, const std::string& name, std::string& error)
{
    std::vector<uint8_t> bytes;
    std::string path = Files::Combine(Files::Combine(root, "Tests"), name + ".txt");
    if (name.empty() || name.find_first_of("/\\:") != std::string::npos || !Files::ReadAll(path, bytes))
    {
        error = "no test " + path;
        return std::nullopt;
    }
    TestRun run;
    run.Name = name;
    run.Folder = Files::Combine(Files::Combine(root, "Tests"), name);
    std::string text(bytes.begin(), bytes.end());
    try
    {
        for (const std::string& raw : Items(text, '\n'))
        {
            if (raw[0] == '#')
                continue;
            size_t eq = raw.find('=');
            if (eq == std::string::npos)
                continue;
            std::string key = Trim(raw.substr(0, eq)), value = Trim(raw.substr(eq + 1));
            if (key == "game")
                run.Game = value;
            else if (key == "frames")
                run.Frames = std::stoull(value);
            else if (key == "every")
                run.Every = std::stoi(value);
            else if (key == "clock")
                run.FixedClock = value != "real";
            else if (key == "loads")
                run.SyncLoads = value == "sync";
            else if (key == "fastzoom")
                run.FastZoom = value == "1";
            else if (key == "times")
            {
                size_t colon = value.find(':');
                if (colon == std::string::npos)
                    throw std::invalid_argument(value);
                run.TimesFrom = std::stoi(value.substr(0, colon));
                run.TimesTo = std::stoi(value.substr(colon + 1));
            }
            else if (key == "saves")
                run.Saves = value;
            else if (key == "press")
                // scnrun's SCNRUN_PRESS: "frame:key[:frames],..." keys held from that frame
                for (const std::string& item : Items(value, ','))
                {
                    size_t a = item.find(':'), b = item.find(':', a + 1);
                    if (a == std::string::npos)
                        continue;
                    int from = std::stoi(item.substr(0, a));
                    int frames = b == std::string::npos ? 3 : std::stoi(item.substr(b + 1));
                    run.m_presses.push_back({from, from + frames - 1, Number(item.substr(a + 1, b == std::string::npos ? std::string::npos : b - a - 1))});
                }
            else if (key == "mouse")
                // scnrun's SCNRUN_MOUSE: "frame:x,y[:buttons[:frames]];..." the mouse there from that
                // frame, the buttons held so many frames
                for (const std::string& item : Items(value, ';'))
                {
                    size_t a = item.find(':'), comma = item.find(',', a + 1), b = item.find(':', a + 1);
                    size_t c = b == std::string::npos ? b : item.find(':', b + 1);
                    if (a == std::string::npos || comma == std::string::npos)
                        continue;
                    Click m{};
                    m.From = std::stoi(item.substr(0, a));
                    m.X = std::stoi(item.substr(a + 1, comma - a - 1));
                    m.Y = std::stoi(item.substr(comma + 1, b == std::string::npos ? std::string::npos : b - comma - 1));
                    m.Buttons = b == std::string::npos ? 0 : std::stoi(item.substr(b + 1, c == std::string::npos ? std::string::npos : c - b - 1));
                    m.To = m.From + (c == std::string::npos ? 3 : std::stoi(item.substr(c + 1))) - 1;
                    run.m_mouse.push_back(m);
                }
        }
    }
    catch (const std::exception&)
    {
        error = "a number that is not one in " + path;
        return std::nullopt;
    }
    if (run.Game.empty() || run.Frames == 0)
    {
        error = path + " has no game= or no frames=";
        return std::nullopt;
    }
    std::stable_sort(run.m_mouse.begin(), run.m_mouse.end(), [](const Click& p, const Click& q) { return p.From < q.From; });
    return run;
}

bool TestRun::KeyDown(int key, int frame) const
{
    for (const auto& p : m_presses)
        if (p.Key == key && frame >= p.From && frame <= p.To)
            return true;
    return false;
}

std::pair<int, int> TestRun::MousePosition(int frame) const
{
    std::pair<int, int> at{0, 0};
    for (const Click& m : m_mouse)
        if (m.From <= frame)
            at = {m.X, m.Y};
    return at;
}

int TestRun::MouseButtons(int frame) const
{
    for (const Click& m : m_mouse)
        if (m.From <= frame && frame <= m.To)
            return m.Buttons;
    return 0;
}

void TestRun::SendMessages(int frame, scripting::ScnVm& vm) const
{
    using scripting::ScnMessage::Key;
    using scripting::ScnMessage::Point;
    namespace Message = scripting::ScnMessage;
    for (const auto& p : m_presses)
    {
        if (p.From == frame)
            vm.WindowMessage(Message::KeyDown, p.Key, Key(0, true, false));
        if (p.To + 1 == frame)
            vm.WindowMessage(Message::KeyUp, p.Key, Key(0, false, false));
    }
    auto [x, y] = MousePosition(frame);
    const std::tuple<int, int, int> buttons[] = {{1, Message::LButtonDown, Message::LButtonUp},
                                                 {2, Message::RButtonDown, Message::RButtonUp},
                                                 {4, Message::MButtonDown, Message::MButtonUp}};
    for (const Click& m : m_mouse)
        for (const auto& [bit, down, up] : buttons)
        {
            if ((m.Buttons & bit) == 0)
                continue;
            if (m.From == frame)
                vm.WindowMessage(down, bit, Point(x, y));
            if (m.To + 1 == frame)
                vm.WindowMessage(up, 0, Point(x, y));
        }
}

std::vector<std::string> TestRun::Report(const Times& times) const
{
    std::vector<std::string> lines;
    lines.push_back(std::format("[times] frames {}-{} ({} run): scripts {:.1f} ms, {:.2f} ms a frame", TimesFrom, TimesTo, times.Frames, times.ScriptsMs,
                                times.Frames > 0 ? times.ScriptsMs / times.Frames : 0.0));
    std::vector<std::pair<std::string, scripting::ScnVm::RoutineTime>> routines(times.Routines.begin(), times.Routines.end());
    std::sort(routines.begin(), routines.end(), [](const auto& a, const auto& b) { return a.second.Milliseconds > b.second.Milliseconds; });
    for (size_t k = 0; k < routines.size() && k < 20; k++)
    {
        const auto& [name, time] = routines[k];
        lines.push_back(std::format("[times] routine {}: {:.1f} ms, {} calls, {:.3f} ms a call", name, time.Milliseconds, time.Calls,
                                    time.Calls > 0 ? time.Milliseconds / time.Calls : 0.0));
    }
    std::vector<std::pair<int, scripting::ScnVm::RoutineTime>> ops(times.Ops.begin(), times.Ops.end());
    std::sort(ops.begin(), ops.end(), [](const auto& a, const auto& b) { return a.second.Milliseconds > b.second.Milliseconds; });
    for (size_t k = 0; k < ops.size() && k < 20; k++)
    {
        const auto& [op, time] = ops[k];
        lines.push_back(std::format("[times] opcode {:04X}: {:.1f} ms, {} calls, {:.3f} ms a call", op, time.Milliseconds, time.Calls,
                                    time.Calls > 0 ? time.Milliseconds / time.Calls : 0.0));
    }
    return lines;
}

void TestRun::SavePicture(scripting::ScnVm& vm, int frame) const
{
    const Bytes& window = vm.Window();
    int w = vm.ScreenWidth, h = vm.ScreenHeight;
    Bytes rgb(size_t(w) * size_t(h) * 3);
    for (size_t i = 0; i + 2 < rgb.size() && i + 2 < window.size(); i += 3)
    {
        rgb[i] = window[i + 2];
        rgb[i + 1] = window[i + 1];
        rgb[i + 2] = window[i];
    }
    size_t length = 0;
    void* png = tdefl_write_image_to_png_file_in_memory_ex(rgb.data(), w, h, 3, &length, 6, MZ_FALSE);
    if (!png)
        return;
    if (std::FILE* f = Files::Open(Files::Combine(Folder, std::format("frame_{:04}.png", frame)), "wb"))
    {
        std::fwrite(png, 1, length, f);
        std::fclose(f);
    }
    mz_free(png);
}

} // namespace openshiina::player
