> **TÀI LIỆU LỊCH SỬ — ĐÃ NGƯNG MAC.** Từ 21/11/2026, chỉ phát triển Windows. Xem [hướng dẫn Windows hiện hành](huong-dan-windows.md). Các lệnh/đường dẫn Mac bên dưới không còn được duy trì.

# Kết quả bổ sung Windows–Mac — 21/11/2026

## Đã triển khai

- Giữ WPF Windows hiện có. Không chuyển/xóa ViewModel, XAML hoặc DesktopClock của WPF. Cấu hình và SQLite của bản cũ không tự di chuyển.
- Thêm `Berkeley.Client` net10.0: runtime node có Start/Stop/Sync/Election/drift; ViewModel không phụ thuộc UI; dùng lại cấu hình, validation, SQLite, demo và Verification bằng source links tới các file hiện có.
- Thêm `Berkeley.CrossPlatform`: Avalonia Desktop 11.3.22, DataGrid 11.3.13 (các gói có chu kỳ phát hành khác nhau). Bốn tab: dashboard, cấu hình, lịch sử, nguyên lý. Có quyền/đồng hồ, sample t1–t4, Step cả Master, log/tìm kiếm/lọc, CSV, chỉnh lệch có xác nhận, quản lý peer và timeout.
- Runtime sử dụng đúng Contracts/Engine/TCP/Bully đang được WPF sử dụng, không thêm giao thức riêng cho Mac. Clock được chọn theo OS. Giao thức hiện chưa xác thực hoặc mã hóa peer; chỉ thử trong LAN tin cậy.
- `MacClock` đọc UTC và monotonic; permission probe chỉ đọc effective UID. Step gọi `settimeofday` trong `/usr/lib/libSystem.B.dylib`, Darwin timeval 64-bit, kiểm tra số hữu hạn/range, ánh xạ errno sang PermissionDenied/Failed. Native boundary có thể giả lập trong test. `ClientClock` khóa chỉnh giờ sau Stop và tách clock demo khỏi hệ điều hành.
- Phiên thử chỉnh giờ thật trên Mac cần `sudo` để có effective UID 0. Không có luồng tự xin quyền, lưu mật khẩu, privileged helper đã ký hoặc notarization. Gói cross-build phải chuẩn bị/chạy thử trên Mac theo hướng dẫn.
- Bản mới dùng thư mục `CrossPlatform` riêng; `demo.db`/`network.db` riêng. Cấu hình dùng cùng schema JSON với WPF. Chuẩn bị `.app`, Info.plist (gồm mô tả Local Network), JIT entitlement, ký ad-hoc cục bộ và script publish cho Apple Silicon/Intel/Windows x64.
- Thêm solution không có WPF để build/test trên Mac. Thêm workflow macOS và mở rộng workflow Windows; chưa chạy trên GitHub vì chưa push.

## Kiểm chứng trên Windows

Build Release cuối: **0 warning, 0 error**. Toàn solution: **101 đạt, 1 bỏ qua, 0 thất bại**:

| Bộ test | Đạt | Bỏ qua |
|---|---:|---:|
| Engine | 49 | 0 |
| Networking | 10 | 0 |
| WPF Desktop | 18 | 0 |
| Client mới | 24 | 1 |

Test bị bỏ qua chỉ đọc API/quyền native macOS và yêu cầu chạy trên Mac. Không có bài test tự động nào chỉnh giờ máy Windows hoặc CI runner.

Các kiểm tra Client mới gồm layout/conversion timeval, permission probe không gọi SetTime, lệch dương/âm, lỗi errno/quyền/giá trị sai, chặn Step sau Stop, clock demo độc lập. Test tích hợp dùng TCP thật và hai clock giả: bootstrap Client mới ↔ bootstrap TCP/Bully/NodeApplication hiện dùng ở WPF, mỗi bên lần lượt làm Master/Worker, lệch +120/-180 giây, hai vòng liên tiếp hội tụ trong ngưỡng test local dưới 100 ms. Không gọi đây là bài thử Windows–Mac vật lý.

Đã kiểm tra ViewModel demo, lịch sử, tìm kiếm/lọc, CSV, cấu hình sai không làm dừng node hoặc ghi đè cấu hình, Stop/restart và đóng khi vòng đang chạy (hủy + flush SQLite). NativeMac API ghi giờ được giả lập để kiểm tra hành vi, chưa thực thi `settimeofday` trên Mac.

Đã mở giao diện Avalonia trực tiếp trên Windows, chụp và xem bố cục; click Đồng bộ ngay, quan sát Measuring → Idle, Success, năm mẫu Worker và sáu Applied; thấy kết quả lưu trong tab lịch sử. Đã chuyển được cả bốn tab, xem form cấu hình và nguyên lý; đóng ứng dụng bằng Alt+F4, xác nhận cửa sổ biến mất. Chưa xác nhận toàn bộ hộp thoại file macOS, Retina/DPI hoặc tải kéo dài.

Script shell được kiểm tra cú pháp bằng Bash; plist được parse XML. Lệnh `--diagnose` chạy trên Windows và trả MacApiSupported=false, kích thước timeval=16, không chỉnh giờ.

Build từng gặp lỗi ghi log của Avalonia build telemetry ngoài sandbox; các lệnh kiểm tra cuối dùng `AVALONIA_TELEMETRY_OPTOUT=1`. Scripts phát hành và CI cũng đặt biến này ở phạm vi tiến trình/job. Không đổi cấu hình telemetry toàn máy. Các gói mới được tải từ NuGet khi được phép; publish cuối dùng cache với runtime .NET 10.0.7.

## Gói thử đã tạo

Trong `artifacts/crossplatform`:

- `BerkeleyTimeSync-osx-arm64-20261121-102640-ebaff3.zip`
- `BerkeleyTimeSync-osx-x64-20261121-102655-35ad29.zip`
- `BerkeleyTimeSync-win-x64-20261121-102703-af11c5.zip`

Đã kiểm tra Mach-O apphost đúng CPU ở từng gói Mac, có runtime/coreclr, AvaloniaNative và SQLite native. DLL Client trong cả ba gói khớp SHA-256 với DLL đã chạy test. Gói có hướng dẫn; manifest tên/size/SHA-256 tại `artifacts/crossplatform/verified-packages.json`. Gói Mac mới chỉ cross-publish, chưa được ký/thực thi trên máy Mac; chạy bước chuẩn bị hoặc build cục bộ theo hướng dẫn.

## Cần người thử xác nhận trên Mac

Làm theo [hướng dẫn macOS](huong-dan-macos.md): chuẩn bị `.app`, chạy demo thường, diagnostic, phiên root, Step 0 rồi độ lệch thật, đủ mẫu/Step và CSV. Thử Windows–Mac theo cả hai vai trò, hai dấu lệch; tiếp theo ba máy có mất Master và sáu máy; kiểm tra Local Network privacy, firewall, sleep/wake, thời gian tự động và phục hồi giờ chuẩn.

Chỉ khi các bước đó đạt mới xác nhận giao diện native macOS, quyền và đồng bộ giờ thật trên cụm hỗn hợp đã được nghiệm thu. Không suy ra điều đó từ cross-build, test fake clock hay kết quả GUI Windows.
