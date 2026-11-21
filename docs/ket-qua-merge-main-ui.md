# Kết quả merge main vào UI và kiểm thử — 21/11/2026

## Merge conflict

Hợp nhất `origin/main` tại `5cd42ae` (bản sửa TV1 `02bb90a`) vào nhánh
`feature/ui` tại `a90a512`. Lúc bắt đầu working tree sạch và chưa có merge đang
chờ; lần pull trước đó đã cập nhật nhánh `main`, chưa đưa vào `feature/ui`.

Ba file xung đột:

- `src/Berkeley.Engine(tv1)/Runtime/WorkerSyncHandler.cs`
- `src/Berkeley.Engine(tv1)/Runtime/SyncRoundOrchestrator.cs`
- `tests/Berkeley.Engine.Tests/WorkerSyncHandlerTests.cs`

Đã kết hợp theo hành vi của cả hai nhánh, không chọn toàn bộ một phía:
giữ cách sửa UTC/spread của TV1 và các kiểm tra monotonic, vòng/phiên/BootId,
đủ mẫu đo lại, hủy vòng và audit Step cho UI đã có trên nhánh UI.

## Kiểm tra ảnh hưởng bản sửa của TV1

| Bản sửa | Tác dụng | Cách kết hợp |
|---|---|---|
| Bỏ `Worker UTC - Master SentAtUtcMs` khi tính tuổi ADJUST | Máy lệch vài phút vẫn nhận được hiệu chỉnh | Giữ; tuổi lệnh được giới hạn bằng monotonic cục bộ từ TIME_REQUEST cùng vòng/phiên/BootId |
| Thêm Master offset 0 vào spread đo lại | Một Worker còn lệch không bị báo spread bằng 0 | Giữ; đồng thời vẫn yêu cầu đủ mẫu của các Worker đã tham gia |

Nhánh UI đã sửa hai lỗi này trước khi merge. Vì vậy bản kết hợp không thay đổi
hành vi đồng bộ so với UI trước merge: phần production chủ yếu bổ sung giải thích
cách kết hợp. Không đổi protocol/JSON, cơ chế TCP/Bully, WindowsClock, giao diện,
cấu hình hay schema SQLite.

Điểm cần tránh: chỉ giữ session và MessageId không làm một ADJUST chưa từng nhận
tự hết hạn. Vì vậy không bỏ toàn bộ kiểm tra tuổi lệnh cục bộ. Lệnh quá hạn, thiếu
TIME_REQUEST phù hợp, sai vòng/phiên/BootId và lệnh trùng vẫn không được Step.

Test của main nói “UTC cũ vẫn Applied” và test UI nói “lệnh hết hạn phải Rejected”
kiểm tra hai tình huống khác nhau. Đã giữ test hết hạn monotonic và thêm ca TV1
với thời hạn cục bộ còn hiệu lực. Đồng thời bổ sung các ca đúng/vượt giới hạn tuổi
trong payload và cấu hình Worker. Không đổi assert của test hết hạn sang Applied.

## Kiểm thử toàn dự án

Build Release: **0 warning, 0 error**.

**98/98 test đạt, 0 thất bại, 0 bỏ qua**:

| Bộ test | Đạt |
|---|---:|
| Berkeley.Engine.Tests | 54 |
| Berkeley.Networking.Tests | 10 |
| Berkeley.Desktop.Tests (WPF cũ) | 18 |
| Berkeley.Client.Tests (Windows mới) | 16 |

Các luồng được kiểm tra:

- ADJUST khi lệch +120/-180 giây, UTC nhảy nhưng monotonic còn hạn, hết hạn thực
  sự, giới hạn tuổi từ cả payload và cấu hình, sai vòng/phiên/BootId và lệnh trùng.
- Spread có Master, residual dương/âm, thiếu/sai mẫu đo lại, Worker ngắt kết nối,
  thiếu mẫu ban đầu; không báo đạt cho phép đo không đầy đủ.
- Hủy ở Adjust/Verifying, lỗi transport, trả về Idle và chạy lại vòng sau.
- TCP thật loopback giữa bootstrap cũ/mới theo cả hai vai trò; ViewModel Windows
  mới kết nối, đồng bộ, hiển thị Step của Worker, lịch sử/CSV và dừng/chạy lại.
- Ba Client: mất Master Node3, bầu Node2 và tiếp tục đồng bộ.
- **Ca mới sáu Client Windows qua TCP thật**: đủ 6/6 kết nối, cùng Master Node6
  và phiên, lệch dương/âm tới vài phút, hai vòng liên tiếp Success; mỗi vòng
  5 mẫu Worker hợp lệ, 0 mẫu loại, 6 Step Applied, spread và khoảng cách giữa
  sáu clock dưới 100 ms trong điều kiện test; lưu đủ hai vòng và xuất CSV.
- Demo, quyền Windows chỉ đọc token, chặn Step sau Stop, cấu hình lỗi được giữ
  nguyên, đóng/hủy/flush SQLite và trạng thái Demo Bully.

TRX của lần kiểm thử nằm tại `tests/*/TestResults/merge-main-ui_*.trx` (bị Git ignore).

Lệnh đã dùng:

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
dotnet build BerkeleyTimeSync.sln -c Release --no-restore
dotnet test BerkeleyTimeSync.sln -c Release --no-build --no-restore --logger 'trx;LogFilePrefix=merge-main-ui'
```

## Kiểm tra giao diện và Windows thật

Đã mở ứng dụng Windows sau merge và nhấn Đồng bộ ngay ở Demo: thấy Measuring →
Sẵn sàng, Success, 5 mẫu Worker/6 Step; spread đo lại **24.0 ms trong lần Demo này**.
Vòng `0f6efad4816346ec9f4e5c3dccd69669` xuất hiện trong tab Lịch sử SQLite, giữ
đúng thời gian bắt đầu/kết thúc và kết quả. Đã đóng cửa sổ qua giao diện.

`--diagnose` nhận Windows x64, đọc UTC và token quyền mà không chỉnh giờ hệ điều
hành. Phiên terminal kiểm tra không có quyền chỉnh giờ. Giao diện Windows mới vẫn
là bản chính; Mac vẫn ngưng phát triển.

Các bài TCP dùng socket thật trên cùng máy với đồng hồ giả; không chỉnh giờ máy
phát triển. Mốc dưới 100 ms trong test và 24 ms ở Demo không phải cam kết trên LAN.
Chưa thực hiện nghiệm thu 2/3/6 máy Windows vật lý, chỉnh giờ hệ điều hành thật,
độ trễ LAN, nhiều mức DPI hay tải dài hạn. Làm theo [hướng dẫn Windows](huong-dan-windows.md)
để nghiệm thu thực tế và phục hồi nguồn giờ chuẩn sau thử.
