# Berkeley Time Sync — Windows

Ứng dụng đồng bộ đồng hồ các máy Windows qua mạng LAN bằng thuật toán Berkeley,
TCP và bầu chọn Master bằng Bully. .NET 10 + Avalonia + SQLite.

**Cập nhật 21/11/2026:** Windows là nền tảng duy nhất được phát triển và đóng gói.
Giao diện Avalonia màu tối là bản Windows chính. Phát triển Mac đã ngưng; mã và
luồng build Mac được lưu tại `archive/macos`, ngoài solution/CI hiện hành.

## Chạy nhanh

Cần Windows 10/11 và .NET 10 SDK. Trong PowerShell:

```powershell
cd D:\HTPT\berkeley-clock-synchronization
.\scripts\run.ps1
```

Hoặc:

```powershell
dotnet run --project .\src\Berkeley.Windows\Berkeley.Windows.csproj -c Release
```

Trong Visual Studio, mở `BerkeleyTimeSync.sln` và chọn **Berkeley.Windows** làm
Startup Project. Cài mới mặc định dùng Demo, không chỉnh giờ Windows; cấu hình đã
lưu sẽ được dùng lại. Lần đầu cần tải các thư viện NuGet chưa có trong cache.

## Chạy trên các máy Windows thật

Mở ứng dụng bằng **Run as administrator**, vào **Cấu hình mạng**, bỏ chọn Demo,
đặt Node ID duy nhất và IP/cổng thật của mỗi peer. Lưu/khởi động lại; chờ đủ máy
online và cùng Master/phiên. Nhấn **ĐỒNG BỘ NGAY** tại Master. Worker tự xử lý lệnh.

Thử hai máy trước, rồi ba máy với mất Master, cuối cùng sáu máy. Kiểm tra từng
Step và spread đo lại; đồng bộ hiện được kích hoạt thủ công. Không dùng IP
`127.0.0.1` để trỏ tới máy khác. Cho phép cổng TCP trên firewall của mạng thử.

[Hướng dẫn chạy, cấu hình hai máy, dữ liệu cũ và nghiệm thu LAN](docs/huong-dan-windows.md).
[Đối chiếu đầy đủ chức năng giao diện WPF và Windows mới](docs/doi-chieu-giao-dien-windows.md).
[Kết quả cập nhật và kiểm chứng](docs/cap-nhat-windows-chinh.md).

## Build, test, tạo gói EXE

```powershell
dotnet build BerkeleyTimeSync.sln -c Release
dotnet test BerkeleyTimeSync.sln -c Release --no-build
.\scripts\publish.ps1
```

ZIP Windows x64 trong `artifacts/release`. Giải nén toàn bộ rồi chạy
`BerkeleyTimeSync.exe`; gói self-contained không cần SDK/runtime trên máy nhận.
Script publish mặc định chỉ phát hành giao diện Windows mới.

## Cấu trúc

| Thành phần | Vai trò |
|---|---|
| `src/Berkeley.Windows` | Giao diện Windows chính, Avalonia |
| `src/Berkeley.Client` | ViewModel và runtime Windows, demo/TCP, clock, lịch sử/CSV |
| `src/Berkeley.Contracts(chung)` | Models và interfaces chung |
| `src/Berkeley.Engine(tv1)` | Berkeley, điều phối vòng và Windows clock |
| `src/Berkeley.Networking(tv2)` | TCP, heartbeat, membership, Bully |
| `src/Berkeley.Desktop(tv3)` | WPF cũ để đối chiếu; một số dịch vụ được Client dùng chung qua source link |
| `tests` | Kiểm thử Engine, Networking, Client và WPF cũ |
| `archive/macos` | Công việc Mac đã ngưng, không nằm trong build/CI |

Bản mới giữ nguyên thư mục dữ liệu của giao diện Avalonia trước đây:
`%LOCALAPPDATA%\BerkeleyTimeSync\CrossPlatform`. Tên thư mục được giữ để bảo toàn
cấu hình/lịch sử; xem hướng dẫn để chuyển cấu hình WPF hoặc dùng `--data-dir`.

Kiểm thử tự động dùng TCP loopback và clock giả; đọc giờ/quyền Windows không đổi
giờ hệ thống. Vẫn cần nghiệm thu trên các máy Windows vật lý. Tài liệu Windows–Mac
cũ chỉ là lịch sử, không phải hướng dẫn hiện hành.
