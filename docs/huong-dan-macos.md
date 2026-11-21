> **TÀI LIỆU LỊCH SỬ — ĐÃ NGƯNG MAC.** Từ 21/11/2026, chỉ phát triển Windows. Xem [hướng dẫn Windows hiện hành](huong-dan-windows.md). Các lệnh/đường dẫn Mac bên dưới không còn được duy trì.

# Chạy Berkeley Time Sync trên macOS và cụm Windows–Mac

## Phạm vi bản mới

Bản Avalonia có dashboard, đồng hồ local/UTC, quyền chỉnh giờ, Start/Stop, Đồng bộ ngay, bầu chọn, chỉnh lệch có xác nhận, cấu hình và quản lý peer, bảng t1–t4/kết quả Step, log có tìm kiếm/bộ lọc, lịch sử SQLite, xuất audit/lịch sử CSV, tab nguyên lý. Giao diện tương đương về nhóm chức năng, không phải bản sao từng pixel của WPF. Bản WPF Windows và script publish cũ vẫn giữ nguyên; hai ứng dụng dùng cấu hình riêng.

Mac và Windows dùng chung Contracts, Berkeley Engine, TCP/Bully, protocol version 1, JSON UTF-8 và frame length big-endian 4 byte. Chọn hệ điều hành không ảnh hưởng ưu tiên bầu chọn: Node ID có ưu tiên cao nhất làm Master. Cần cùng bản Engine đã sửa ngày 21/11/2026 trên mọi máy.

Bản này dành cho kiểm thử trên LAN tin cậy. Peer chưa được xác thực/mã hóa; không mở cổng ra Internet. macOS dùng `settimeofday` thật, không chỉ sửa đồng hồ hiển thị. Để thử quyền chỉnh giờ, bản hiện tại cần chạy ứng dụng với effective UID 0 qua `sudo`; chưa có privileged helper ký bằng Developer ID. Đây chưa phải bản phân phối đã notarize hoặc nghiệm thu Mac thật.

## Chọn bản và build

- Mac Apple Silicon (M1/M2/...): `osx-arm64`.
- Mac Intel: `osx-x64`.
- Mục tiêu hỗ trợ của gói là macOS 14 trở lên theo nền tảng .NET 10. Gói self-contained không yêu cầu cài .NET để chạy; build từ nguồn cần .NET 10 SDK.
- Windows vẫn chạy EXE WPF cũ. Có thêm gói Avalonia `win-x64` nếu muốn dùng cùng giao diện với Mac. Không chạy hai ứng dụng cùng Node ID/cổng trên một máy.

### Build tại Mac (ưu tiên cho lần xác nhận đầu tiên)

Mở Terminal trong thư mục repository:

```bash
dotnet build BerkeleyTimeSync.CrossPlatform.sln -c Release
dotnet test BerkeleyTimeSync.CrossPlatform.sln -c Release
bash scripts/publish-macos.sh
```

Script tự chọn kiến trúc máy, publish, tạo `.app`, ký ad-hoc cho phát triển cục bộ và ZIP vào `artifacts/crossplatform`. Có thể chỉ định `bash scripts/publish-macos.sh osx-arm64` hoặc `osx-x64`. Không build toàn solution Windows trên Mac vì solution đó còn WPF.

### Gói được cross-build từ Windows

Giải nén ZIP đúng kiến trúc, giữ `.app`, `prepare-mac.sh`, `entitlements.plist` trong cùng thư mục. Chạy trên Mac, **không dùng sudo cho bước chuẩn bị**:

```bash
cd "/duong-dan/thu-muc-goi-da-giai-nen"
bash prepare-mac.sh
```

Script đặt quyền thực thi cho apphost và ký ad-hoc các thư viện native cùng app để chuẩn bị bản lab cục bộ. Không chỉnh Gatekeeper/quarantine. Ad-hoc không thay thế Developer ID/notarization. Nếu macOS vẫn chặn gói được chuyển từ máy khác, giữ nguyên bảo vệ hệ thống và build từ nguồn trên Mac theo mục trên; lưu nguyên thông báo lỗi để chẩn đoán.

## Kiểm tra trước khi chỉnh giờ

Trong thư mục gói đã giải nén/chuẩn bị:

```bash
APP="$PWD/BerkeleyTimeSync.app"
"$APP/Contents/MacOS/BerkeleyTimeSync" --diagnose > diagnostic-user.json
"$APP/Contents/MacOS/BerkeleyTimeSync"
```

`--diagnose` chỉ đọc OS/kiến trúc/UTC/khả năng gọi API/quyền, không gọi Step. Chạy GUI ở chế độ mặc định Demo trước. Kiểm tra đủ bốn tab, đồng hồ cập nhật, các nút, nhập cấu hình, cuộn bảng, vị trí cửa sổ ở Retina/DPI, tiếng Việt, lưu file bằng hộp thoại macOS. Chạy một vòng demo: phải có mẫu đo, kết quả Step, spread sau, log và lịch sử; CSV phải mở được. Demo không chứng minh chỉnh giờ Mac thật.

Đóng ứng dụng bằng Quit hoặc nút đóng cửa sổ để dừng node và flush SQLite. Kiểm tra mở lại còn lịch sử. Ứng dụng đang có cửa sổ thiết lập lỗi sẽ giữ nguyên file cấu hình hỏng để sửa, không tự ghi đè.

## Chạy có quyền chỉnh giờ thật

Trên máy Mac thử, mở Terminal trong thư mục gói. Đóng bản GUI thường trước, rồi:

```bash
APP="$PWD/BerkeleyTimeSync.app"
sudo "$APP/Contents/MacOS/BerkeleyTimeSync" --diagnose > diagnostic-root.json
sudo "$APP/Contents/MacOS/BerkeleyTimeSync" \
  --data-dir "/var/root/Library/Application Support/BerkeleyTimeSyncLab"
```

Mật khẩu do `sudo` xử lý trực tiếp trong Terminal, ứng dụng không hỏi/lưu mật khẩu. Không dùng `sudo open ...`: lệnh đó không bảo đảm tiến trình GUI có effective UID 0. Nếu cửa sổ GUI có quyền root không mở được trong phiên macOS của bạn, lưu thông báo Terminal và diagnostic, không coi đây là đã chạy thành công. Không tự tắt SIP/Gatekeeper hoặc bật quyền hệ thống khác để vượt lỗi.

Thư mục root ở lệnh trên có cấu hình/lịch sử riêng. Những thiết lập bạn lưu khi chạy thường **không tự xuất hiện** ở phiên root: nhập lại trong GUI root. Trong tab cấu hình, bỏ chọn Demo rồi lưu/khởi động lại. Badge phải hiển thị quyền chỉnh giờ; kết quả Step thực tế vẫn có thể Failed/PermissionDenied và phải được ghi nhận.

Để xác nhận đường gọi API trước bài thử lớn: trong chế độ thật, nhập `0` vào số phút và bấm Chỉnh lệch, đọc hộp xác nhận rồi thực hiện. Phải có `Applied` trong log. Step 0 chỉ xác nhận API/quyền, không chứng minh hội tụ. Tiếp theo mới thử lệch +2/-3 phút trên máy thử. Các lệnh đồng bộ Berkeley thay đổi giờ khi bạn bấm Đồng bộ ngay tại Master; Worker nhận ADJUST hợp lệ sẽ chỉnh giờ mà không hỏi từng lệnh.

## Cấu hình hai máy Windows–Mac

Ví dụ thay bằng IP LAN **thực tế**:

| Máy | Node ID | Listen address / port | Peer |
|---|---|---|---|
| Windows | Node1 | 0.0.0.0:5000 | Node2 → IP Mac:5000 |
| Mac | Node2 | 0.0.0.0:5000 | Node1 → IP Windows:5000 |

`0.0.0.0` chỉ là địa chỉ lắng nghe, không dùng làm địa chỉ peer. `127.0.0.1` chỉ dùng cho thử trên cùng máy. Hai máy độc lập có thể dùng cùng cổng 5000; trên cùng máy phải có cổng và thư mục dữ liệu khác nhau. Khi thử hai máy hãy chỉ cấu hình một peer trên mỗi máy.

Kiểm tra TCP hai chiều và cấu hình firewall do người quản trị thực hiện nếu cần. Ví dụ:

Trên macOS có Local Network privacy, người thử cần cho phép truy cập mạng cục bộ khi hệ thống yêu cầu. Bundle có mô tả `NSLocalNetworkUsageDescription`. Quyền này khác với quyền root để chỉnh giờ và khác firewall; quyền đã cấp cho phiên thường không bảo đảm phiên root có cùng trạng thái. Nếu kết nối bị chặn, ghi lại trạng thái quyền và lỗi. Bản ad-hoc chưa có danh tính ký Apple ổn định như bản Developer ID. Xem [hướng dẫn Local Network privacy của Apple](https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy).

```powershell
# Windows → Mac
Test-NetConnection <IP-Mac> -Port 5000
```

```bash
# Mac → Windows
nc -vz <IP-Windows> 5000
```

Không dùng dấu `<...>` nguyên văn, thay IP thực. Giữ Windows/Mac cùng LAN hoặc mạng định tuyến tới nhau; Wi-Fi có client isolation có thể chặn dù cùng tên mạng.

### Các bước nghiệm thu hai máy

1. Chạy Windows với quyền Administrator, Mac bằng lệnh root trên. Bỏ chọn Demo ở cả hai máy, lưu cấu hình đúng peer. Chờ đủ **2/2 online** và cùng Master/phiên. Ví dụ trên Mac Node2 làm Master.
2. Ghi trạng thái nguồn đồng bộ giờ tự động trước thử. Người thử chủ động kiểm soát nguồn đó trên cả hai máy để không tranh chấp với Berkeley. Không coi việc nhảy lại do dịch vụ giờ chuẩn là lỗi TCP.
3. Chỉnh lệch +2 phút ở Worker, xác nhận đổi UTC hệ điều hành thật. Bấm Đồng bộ ngay tại Master. Yêu cầu một mẫu Worker hợp lệ, **hai kết quả Applied**, spread đo lại có giá trị và dưới ngưỡng nhóm thống nhất. So sánh giờ hệ điều hành của cả hai máy, không chỉ nhìn đồng hồ GUI.
4. Lặp lại với -3 phút, chạy thêm vòng thứ hai. Thử thiếu quyền: chạy Mac không sudo ở chế độ thật để xác nhận `PermissionDenied` và vòng không báo thành công giả.
5. Đổi Node ID/peer để Windows là Node2 (Master), Mac Node1 (Worker), thử lại cả hai dấu lệch. Không chỉ kiểm tra một hệ điều hành làm Master.
6. Dừng Worker, chạy vòng lỗi, khởi động lại rồi chạy lại. Đóng/mở ứng dụng; xác nhận lịch sử và CSV. Khi chưa có Worker, một vòng Failed là hợp lý và vòng sau không bị kẹt.
7. Xuất audit/lịch sử tại Master, ghi phiên, IP/cổng, độ lệch trước/sau, số mẫu và kết quả từng Step. Phục hồi nguồn đồng bộ giờ chuẩn khi xong. CSV/DB tạo bởi phiên root có thể chỉ truy cập được với quyền root; xuất CSV bằng GUI tới thư mục thử thích hợp và giữ bản sao trước khi đổi dữ liệu.

### Ba rồi sáu máy

Ba máy: đủ 3/3 online, hai mẫu Worker hợp lệ, ba Step Applied. Dừng Master, chờ hai node còn lại thống nhất Master/phiên mới rồi đồng bộ tiếp; khởi động lại node cũ. Sáu máy: đủ 6/6, năm mẫu hợp lệ, sáu Step Applied. Kết quả trên số node đang tham gia không chứng minh các peer offline đã hội tụ.

Ngưỡng mặc định 60 giây là cấu hình, không phải cam kết độ chính xác. Test loopback dưới 100 ms không thay thế số đo LAN. Kiểm tra thêm sleep/wake Mac, đổi Wi-Fi/IP, lỗi cổng bị chiếm, round timeout và cửa sổ ở các kích thước/DPI.

## Dữ liệu và cách báo lỗi

- Chạy thường: thư mục do GUI hiển thị; mặc định `<LocalApplicationData>/BerkeleyTimeSync/CrossPlatform` theo .NET trên hệ điều hành đang chạy.
- Chạy root theo hướng dẫn: `/var/root/Library/Application Support/BerkeleyTimeSyncLab`.
- `node_config.json`: cùng cấu trúc với WPF; có thể sao chép có chủ đích khi mọi node đã dừng, phải kiểm tra lại Node ID/IP/cổng trước chạy.
- `demo.db` và `network.db`: tách dữ liệu theo chế độ. WPF vẫn dùng DB cũ, không tự nhập sang bản mới.
- Gửi lại: loại CPU Mac, phiên bản macOS, diagnostic-user/root, bước tái hiện, ảnh màn hình, audit CSV/history CSV và lỗi Terminal. Không gửi mật khẩu.

`MacApiSupported=false` thường là sai nền tảng/kiến trúc. `MacSetTimePermission=false` ở phiên thường là dự kiến; ở phiên root phải xem lệnh chạy/diagnostic. `errno 1/13` là bị từ chối quyền, `errno 22` là API từ chối tham số. `Address already in use` cần dừng node đang dùng cổng hoặc đổi cổng. TCP refusal thường là chưa chạy peer hoặc sai IP/cổng; timeout có thể do đường mạng/firewall.

## Nguồn kỹ thuật

- [Avalonia macOS packaging](https://docs.avaloniaui.net/docs/deployment/macos): cấu trúc `.app` và yêu cầu ký mã.
- [Microsoft .NET trên macOS](https://learn.microsoft.com/en-us/dotnet/core/install/macos): nền tảng và kiến trúc hỗ trợ.
- [Apple Darwin timeval](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/_types/_timeval.h), [settimeofday và kiểm tra quyền](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/kern/kern_time.c): biên API native được sử dụng.
