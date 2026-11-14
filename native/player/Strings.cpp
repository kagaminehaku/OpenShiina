// The Switch player's own words (see Strings.h)
#include "Strings.h"

#include <string_view>
#include <unordered_map>

namespace openshiina::player {

const LanguageChoice kLanguageChoices[3] = {
    {"", "The console's (English)"},
    {"en", "English"},
    {"vi", "Tiếng Việt"},
};

namespace {

bool s_vietnamese = false;

// English, Vietnamese
constexpr std::pair<const char*, const char*> kVietnamese[] = {
    // the buttons' hints
    {"OK", "OK"},
    {"Back", "Quay lại"},
    {"Cancel", "Hủy"},
    {"Change", "Đổi"},
    {"Go on", "Tiếp tục"},
    {"Choose", "Chọn"},
    {"Play", "Chơi"},
    {"Game", "Game"},
    {"Move", "Chuyển"},
    {"Look again", "Quét lại"},
    {"Settings", "Cài đặt"},
    {"Quit", "Thoát"},
    {"Yes", "Có"},
    {"No", "Không"},
    {"On", "Bật"},
    {"Off", "Tắt"},

    // the settings
    {"Scaling", "Cách phóng hình"},
    {"FSR sharpness", "Độ nét FSR"},
    {"File cache", "Bộ nhớ đệm file"},
    {"Show the frame rate", "Hiện tốc độ khung hình"},
    {"Fast zoom", "Zoom nhanh"},
    {"Fast zoom: {}", "Zoom nhanh: {}"},
    {"Zooms of the picture on the GPU in fewer steps: faster, very slightly different from the PC.",
     "Phóng to / thu nhỏ hình trên GPU với ít bước hơn: nhanh hơn, khác bản PC rất ít."},
    {"Language", "Ngôn ngữ"},
    {"The console's (English)", "Theo máy (English)"},
    {"Auto ({} MB here)", "Tự động ({} MB trên máy này)"},
    {"1 mild ... 10 strongest (for FSR 1 only).", "1 nhẹ ... 10 mạnh nhất (chỉ cho FSR 1)."},
    {"Frames a second and the slowest frame, over the game's corner.", "Số khung hình mỗi giây và khung hình chậm nhất, ở góc màn hình game."},
    {"The words of OpenShiina's own screens. The games keep their own.", "Ngôn ngữ của các màn hình OpenShiina. Game vẫn giữ ngôn ngữ của game."},
    {"From the next game started.", "Có tác dụng từ game mở sau."},
    {"Sharp", "Sắc nét"},
    {"Whole game pixels as far as they fit, the rest smooth (even text and lines).",
     "Giữ nguyên điểm ảnh game trong phần vừa khít, phần còn lại làm mịn (chữ và nét đều nhau)."},
    {"Whole numbers only", "Chỉ số nguyên"},
    {"x2, x3... with black around (exact).", "x2, x3… có viền đen xung quanh (chính xác)."},
    {"Smooth", "Mịn"},
    {"Filtered, soft.", "Có lọc, mềm."},
    {"Nearest", "Gần nhất"},
    {"The screen's size, pixels as they fall (uneven when not a whole number).",
     "Vừa kích thước màn hình, điểm ảnh rơi đâu lấy đó (không đều khi tỉ lệ không nguyên)."},
    {"AMD's upscaler, clean edges, sharpened as set below (the GPU's work).",
     "Bộ phóng hình của AMD, viền sạch, làm nét theo mức bên dưới (GPU xử lý)."},
    {"Auto", "Tự động"},
    {"By the device's memory: a thirty-second of it, 64 MB to 512 MB.", "Theo RAM của máy: 1/32 dung lượng, từ 64 MB đến 512 MB."},
    {"For the oldest devices: pictures over 16 MB are read again each time.", "Cho máy cũ nhất: hình trên 16 MB phải đọc lại mỗi lần."},
    {"Light: pictures over 32 MB are read again each time.", "Nhẹ: hình trên 32 MB phải đọc lại mỗi lần."},
    {"Pictures of up to 64 MB are kept.", "Giữ lại hình đến 64 MB."},
    {"The largest event pictures are kept too.", "Giữ cả những hình sự kiện lớn nhất."},
    {"For devices with plenty of memory.", "Cho máy nhiều RAM."},

    // the list of games
    {"Games", "Game"},
    {"Looking at the games...", "Đang tìm game..."},
    {"No game folder in {} yet", "Chưa có thư mục game nào trong {}"},
    {"(not recognised)", "(không nhận ra)"},
    {"console", "bộ nhớ máy"},

    // a game's own screen
    {"Measuring {}...", "Đang đo {}..."},
    {"Choose its scheme", "Chọn scheme cho game"},
    {"Choose another scheme", "Chọn scheme khác"},
    {"Delete the game (frees {})", "Xóa game (giải phóng {})"},
    {"Delete the game (frees {}), keep its saves", "Xóa game (giải phóng {}), giữ save"},
    {"Delete the game and its saves ({})", "Xóa game và cả save ({})"},
    {"Played {}", "Chơi {}"},
    {"Not played yet", "Chưa chơi"},
    {"Played for {}", "Đã chơi {}"},
    {"Size {}, saves {}", "Dung lượng {}, save {}"},
    {"Size {}, no saves yet", "Dung lượng {}, chưa có save"},
    {"On the console: {}", "Trong bộ nhớ máy: {}"},
    {"On the SD card: {}", "Trên thẻ SD: {}"},
    {"Scheme chosen: {}", "Scheme đã chọn: {}"},
    {"Not recognised: its .exe does not tell the scheme.", "Không nhận ra: file .exe không cho biết scheme."},
    {"Trying the schemes on {}...", "Đang thử các scheme trên {}..."},
    {"No scheme of Formats.Json decodes its archives' files.", "Không scheme nào trong Formats.Json giải mã được file của game."},
    {"{}  ({} of {} files right)", "{}  (đúng {} / {} file)"},
    {"The schemes that decode it", "Các scheme giải mã được"},
    {"Delete the game", "Xóa game"},
    {"Its folder and its saves are deleted: they cannot be brought back.", "Thư mục game và save sẽ bị xóa, không lấy lại được."},
    {"Its folder is deleted: it cannot be brought back.", "Thư mục game sẽ bị xóa, không lấy lại được."},
    {"Its saves are kept ({}).", "Save vẫn được giữ ({})."},
    {"The folder has no .WAR archives in it any more, so it was left alone.", "Thư mục không còn file .WAR nào nên đã được giữ nguyên."},
    {"Deleting {}...", "Đang xóa {}..."},
    {"{} was deleted: {} freed.", "Đã xóa {}: giải phóng {}."},
    {"Not everything could be deleted.", "Không xóa được hết."},

    // play times, when played
    {"under a minute", "chưa tới 1 phút"},
    {"{} min", "{} phút"},
    {"{} h", "{} giờ"},
    {"{} h {} min", "{} giờ {} phút"},
    {"just now", "vừa xong"},
    {"a minute ago", "1 phút trước"},
    {"{} minutes ago", "{} phút trước"},
    {"an hour ago", "1 giờ trước"},
    {"{} hours ago", "{} giờ trước"},
    {"yesterday", "hôm qua"},
    {"{} days ago", "{} ngày trước"},
    {"on {}", "ngày {}"},

    // moving a game
    {"the console's storage", "bộ nhớ máy"},
    {"the SD card", "thẻ SD"},
    {"{} has a folder named {} already.", "{} đã có thư mục tên {}."},
    {"Not all of its folder can be read.", "Không đọc được hết thư mục game."},
    {"It has a file of 4 GB or more: the console's storage (FAT32) cannot hold it.",
     "Game có file từ 4 GB trở lên: bộ nhớ máy (FAT32) không chứa được."},
    {"It needs {}; {} has {} free.", "Game cần {}; {} còn trống {}."},
    {"It needs {} and 1 GB kept free; {} has {} free.", "Game cần {} và chừa 1 GB; {} còn trống {}."},
    {"The console's storage", "Bộ nhớ máy"},
    {"The game goes into the console's own storage{}: its USER partition, beside the system's games and saves. Before the first one:",
     "Game sẽ vào bộ nhớ trong của máy{}: phân vùng USER, cạnh game và save của hệ thống. Trước lần đầu:"},
    {"- Keep the console charged: switched off while copying, that partition can be damaged.",
     "- Giữ máy đủ pin: tắt máy khi đang chép có thể làm hỏng phân vùng đó."},
    {"- 1 GB is kept free there: the system needs room for its saves and updates.",
     "- Luôn chừa 1 GB trống: hệ thống cần chỗ cho save và bản cập nhật."},
    {"- A format or a factory reset of the console deletes the games there.", "- Format hoặc khôi phục cài đặt gốc sẽ xóa các game ở đó."},
    {"- Only the folder OpenShiina/Games is written; nothing else there changes.",
     "- Chỉ ghi vào thư mục OpenShiina/Games; những thứ khác ở đó không đổi."},
    {"This is shown once.", "Thông báo này chỉ hiện một lần."},
    {"{} in {} files, to {}.", "{} trong {} file, chuyển sang {}."},
    {"Free there now: {}.", "Ở đó đang trống: {}."},
    {"The game is copied, then deleted from where it was.", "Game được chép sang, rồi xóa ở chỗ cũ."},
    {"Moving to {}", "Đang chuyển sang {}"},
    {"{} of {}", "{} / {}"},
    {"{} of {}, {:.0f} MB a second", "{} / {}, {:.0f} MB mỗi giây"},
    {"Cancelled: it stays where it was.", "Đã hủy: game vẫn ở chỗ cũ."},
    {"Not moved (it stays where it was): {}.", "Không chuyển được (game vẫn ở chỗ cũ): {}."},
    {"Deleting it from where it was...", "Đang xóa ở chỗ cũ..."},
    {"Copied, but not all of the old folder could be deleted: {}", "Đã chép xong, nhưng không xóa được hết thư mục cũ: {}"},

    // while a game plays
    {"Paused", "Tạm dừng"},
    {"Back to the game", "Quay lại game"},
    {"Scaling: {}", "Cách phóng hình: {}"},
    {"FSR sharpness: {}", "Độ nét FSR: {}"},
    {"Leave the game", "Thoát game"},
    {"{:.0f} fps, slowest {:.1f} ms", "{:.0f} fps, chậm nhất {:.1f} ms"},
};

const std::unordered_map<std::string_view, const char*>& Table()
{
    static const std::unordered_map<std::string_view, const char*> table(std::begin(kVietnamese), std::end(kVietnamese));
    return table;
}

} // namespace

void SetLanguage(const std::string& code) { s_vietnamese = code == "vi"; }

bool Vietnamese() { return s_vietnamese; }

std::string VietnameseWords()
{
    std::string words;
    for (const auto& [english, vietnamese] : kVietnamese)
        words += vietnamese;
    return words;
}

const char* Tr(const char* english)
{
    if (!s_vietnamese)
        return english;
    auto found = Table().find(english);
    return found != Table().end() ? found->second : english;
}

} // namespace openshiina::player
