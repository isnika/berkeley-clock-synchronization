# Context và tiến độ dự án — góc nhìn TV3

Cập nhật: **20/11/2026** (Asia/Bangkok). Người dùng là **thành viên 3 (TV3)**.

Phân tích tiếp theo theo yêu cầu người dùng: [Kế hoạch hoàn thiện TV3](ke-hoach-hoan-thien-tv3.md). Phạm vi kiểm tra/cập nhật tập trung Desktop, cấu hình, lưu trữ và đóng gói; mã TV1/TV2 chỉ xem xét, chưa sửa. Kế hoạch phân biệt rõ đề xuất chưa triển khai và các lỗi TV3 đã tái hiện.

## Phạm vi và nguồn kiểm chứng

- Repository: `D:\HTPT\berkeley-clock-synchronization`.
- Đọc bản văn bản của tài liệu phân công và tổng quan tại `D:\HTPT\phan_cong_code.txt` và `D:\HTPT\tong_quan_do_an.txt`, README, tài liệu phân tích, mã nguồn và các bộ test.
- Kiểm tra trên nhánh `main`, commit `0eba6d6` ngày 19/11/2026: `implement TCP transport and Bully elec`.
- Đây là trạng thái checkout cục bộ tại thời điểm kiểm tra; chưa truy vấn GitHub để xác nhận có thay đổi mới hơn.
- Tài liệu `phan-tich-do-an-berkeley.md` được viết theo góc nhìn TV1, còn dùng từ “bạn” và đánh dấu nhiều mục hoàn thành. Không dùng các dấu tick đó làm bằng chứng nghiệm thu hoặc xác định vai trò người dùng.
- Lần cập nhật này chỉ bổ sung tài liệu context và liên kết README; chưa sửa logic ứng dụng.

## Bối cảnh và yêu cầu đã thống nhất

Đồ án môn Các Hệ Thống Phân Tán: một ứng dụng Node chạy trên Windows, có thể giữ vai trò Master hoặc Worker, đồng bộ đồng hồ Windows thật bằng Berkeley. Quy mô thiết kế là 6 máy độc lập, gồm 1 Master và 5 Worker; được phép dùng ít máy hơn trong quá trình phát triển.

- C# / .NET 10 LTS, WPF theo MVVM, TCP với JSON UTF-8 có tiền tố độ dài 4 byte, SQLite cục bộ.
- Step chỉnh trực tiếp giờ Windows trên cả Master và Worker; đồng hồ giả phục vụ kiểm thử.
- Trung bình gồm Master với offset 0 và các Worker có mẫu hợp lệ; không loại máy chỉ vì lệch vài phút.
- Offset dương nghĩa Worker nhanh hơn Master; hiệu chỉnh dương nghĩa chỉnh tiến. Timestamp UTC và đơn vị mili giây.
- Bully bầu Master; đổi Master phải hủy vòng cũ và đo mới. Heartbeat và timeout phải dùng thời gian monotonic.
- GUI cần thể hiện vai trò, kết nối, thông điệp gửi/nhận, RTT, offset, mức chỉnh, lỗi và kết quả đo lại.
- “Đã áp dụng Step” và “đã đạt ngưỡng sai số” là hai kết quả riêng. Test loopback và dữ liệu giả chưa chứng minh đồng bộ nhiều đồng hồ Windows độc lập.
- Cách hiểu “đồng bộ bị động” và tiêu chí “chính xác tới phút” vẫn cần xác nhận theo tài liệu gốc. Giá trị 60 giây trong cấu hình hiện tại là lựa chọn của implementation, chưa phải bằng chứng ngưỡng nghiệm thu đã được thầy chốt.

## Phân công và ranh giới module

| Thành viên | Trách nhiệm | Hợp đồng chính |
|---|---|---|
| TV1 | Contracts, phép tính Berkeley, WindowsClock, xử lý Worker và điều phối vòng | Cung cấp `ISystemClock`, `IClockReader`, `INodeApplication` |
| TV2 | TCP, framing, danh tính Node, membership, heartbeat, Bully | Cung cấp `INetworkTransport`, `ILeadershipService` |
| **TV3 — người dùng** | **WPF, cấu hình, quan sát trạng thái, SQLite, lịch sử/CSV, bootstrap, CI, đóng gói** | Dùng `INodeApplication`, triển khai `IAuditSink`, ghép các module |

ViewModel gửi command và hiển thị snapshot/sự kiện; logic Berkeley, socket và áp dụng hiệu chỉnh nghiệp vụ thuộc module tương ứng. GUI cần chuyển cập nhật về WPF Dispatcher và tránh chặn luồng I/O.

Luồng chính: GUI gọi Đồng bộ ngay → Engine mở vòng → Transport trao đổi TIME_REQUEST/TIME_RESPONSE → Engine tính trung bình, gửi ADJUST và chỉnh Master → Worker trả ADJUST_RESULT → đo lại → RoundSummary → GUI và SQLite.

## Tiến độ đã quan sát

| Phần | Trạng thái có bằng chứng | Giới hạn xác nhận |
|---|---|---|
| Contracts / Engine (TV1) | Có model, interface, calculator, WindowsClock/FakeClock, WorkerSyncHandler, SyncRoundOrchestrator, NodeApplication; **31/31 test đạt** | Chưa kiểm chứng Step thật nhiều máy trong lần rà soát này |
| Networking (TV2) | Có TcpNetworkTransport, IPeerLiveness và BullyLeadershipService; **8/8 test đạt**, gồm loopback, bầu lại 3 Node và gọi Engine sau bầu lại | GUI hiện chưa dùng các implementation này; chưa kết luận toàn bộ B1–B10 đã nghiệm thu |
| Desktop (TV3) | Có giao diện, cấu hình JSON, dữ liệu trạng thái, log/lọc, SQLite, lịch sử và CSV; **7/7 test đạt** | Test hiện bao phủ một số cấu hình/ViewModel và thao tác SQLite, chưa bao phủ toàn bộ MainViewModel và vòng đời UI |
| Build | Solution build thành công trên Windows x64 với SDK **10.0.203**, **0 lỗi** | Có cảnh báo NuGet, xem phần kiểm chứng |
| Triển khai / nghiệm thu | Chưa thấy pipeline CI, publish profile, script đóng gói hoặc dữ liệu thực nghiệm nhiều máy trong các file đã kiểm tra | Chưa xác nhận mốc 4–6 ngoài thực tế |

Các mốc Git gần nhất: Engine hoàn thiện thêm runtime/test ngày 13/11; UI `feature/ui` merge vào `main` ngày 18/11 (`1f3e7ae`, commit UI `1126efd`); Networking được thêm ngày 19/11 (`0eba6d6`). Không suy diễn phần trăm hoàn thành từ số commit hay test.

## Đối chiếu đầu việc TV3 (C1–C10)

| Mã | Đầu việc | Trạng thái hiện tại |
|---|---|---|
| C1 | Nhập, kiểm tra, lưu Node/peers | Đã có form, thêm/xóa peer và JSON; chưa thấy validation đầy đủ; lỗi đọc/ghi cấu hình bị bỏ qua |
| C2 | Giờ Windows, trạng thái quyền | Đã có đồng hồ cập nhật 50 ms và kiểm tra quyền qua WindowsClock; chưa chạy GUI để xác minh hành vi thực tế |
| C3 | Vai trò, Master, bầu chọn | Đã có binding từ snapshot và thao tác mô phỏng bầu chọn; cần ghép Networking TV2 và kiểm chứng sự kiện thật |
| C4 | Start, Stop, Đồng bộ ngay | Có tự khởi động, Đồng bộ ngay và lưu cấu hình để restart; chưa thấy command/nút Start–Stop riêng |
| C5 | Bảng Node, RTT, offset, hiệu chỉnh | Đã có NodeCardViewModel, bảng mẫu và cập nhật snapshot; cần kiểm chứng độ mới, trạng thái offline và dữ liệu từ mạng thật |
| C6 | Nhật ký gửi/nhận/lỗi | Có lọc, tìm kiếm, xuất CSV; danh sách hiển thị giới hạn 300, bộ đệm lọc giới hạn 1.000 sự kiện |
| C7 | Tổng kết vòng, kết quả đo lại | Đã có nhưng còn lỗi: thiếu số đo lại vẫn có thể hiển thị hội tụ/đạt ngưỡng |
| C8 | SQLite và hàng đợi ghi | Có IAuditSink và Channel ghi nền; lỗi bị nuốt, queue không giới hạn, RoundSummary ghi bằng task riêng chưa được chờ khi đóng |
| C9 | Tra cứu và xuất dữ liệu | Có 50 vòng gần nhất, đọc audit và xuất CSV; xuất lịch sử UI dựa trên danh sách đang nạp, chưa phải toàn bộ lịch sử DB |
| C10 | Bootstrap, build, CI, đóng gói | Build được, ghép module trong MainViewModel; còn thiếu tích hợp Networking TV2 và bằng chứng CI/bản phát hành chạy máy độc lập |

## Các điểm cần ưu tiên tiếp theo cho TV3

1. **Ghép Networking TV2.** `MainViewModel.InitializeNodeApplication()` hiện chọn `SimulatedClusterTransport` hoặc `Desktop.Services.RealNetwork.RealTcpNetworkTransport` và `RealBullyLeadershipService`. Dù Desktop đã tham chiếu project Networking, nhánh mạng thật chưa dùng `Berkeley.Networking.TcpNetworkTransport` / `BullyLeadershipService` vừa được TV2 bàn giao. Cần thống nhất một bộ implementation và quản lý rõ Start/Stop/Dispose. `NodeApplication.StopAsync()` hiện chỉ dừng Engine, không tự dừng transport/leadership.
2. **Sửa đánh giá kết quả thiếu dữ liệu.** `OnRoundCompleted()` đang dùng `(summary.RemeasuredSpreadMs ?? 0)` và chuỗi dự phòng `< 10.0 ms (Hội tụ)`. Khi không có phép đo lại, phải hiển thị chưa có dữ liệu kiểm chứng và không kết luận đạt ngưỡng.
3. **Hoàn thiện vòng đời ứng dụng.** Khi lưu cấu hình để restart, dịch vụ cũ chưa được Dispose đầy đủ; audit handler được đăng ký lại mỗi lần khởi tạo. Chưa thấy cleanup khi đóng MainWindow. Cần thêm Start/Stop, hủy đăng ký sự kiện và chờ flush SQLite khi thoát.
4. **Validation và phản hồi lỗi.** Kiểm tra Node ID/peer trùng, địa chỉ, cổng, timeout và ngưỡng trước khi áp dụng. AppConfigManager và SqliteAuditSink hiện bỏ qua nhiều lỗi nên GUI có thể báo thành công trong khi dữ liệu chưa được lưu. Cập nhật UI dùng Dispatcher.Invoke đồng bộ cũng cần xem lại để tránh trì hoãn I/O.
5. **Tách chế độ demo và tác động giờ thật cho rõ.** Mặc định `UseSimulatedCluster = true`, nhưng MainViewModel vẫn tạo WindowsClock; nút QuickDrift gọi ApplyStep trực tiếp. Chế độ giả lập mạng hiện không đồng nghĩa đồng hồ hệ điều hành được giả lập. Đây là hành vi đọc được từ mã, chưa thao tác chỉnh giờ trong lần kiểm tra này.
6. **Hoàn thiện kiểm thử, CI, đóng gói và bàn giao.** Đưa Networking.Tests vào solution, bổ sung kiểm tra các nhánh UI quan trọng nêu trên, chuẩn bị cấu hình mẫu theo IP thực, bản publish self-contained/ZIP, hướng dẫn chạy và thu log/CSV trước–sau trên 2, 3 rồi 6 máy độc lập.

## Kết quả kiểm chứng ngày 20/11/2026

```powershell
dotnet build BerkeleyTimeSync.sln --nologo -v minimal
# Thành công: 0 lỗi, 5 warning NU1903 (cùng dependency xuất hiện ở nhiều project).

dotnet test BerkeleyTimeSync.sln --no-build --no-restore --nologo -v minimal
# Engine: 31 đạt; Desktop: 7 đạt; 0 thất bại, 0 bỏ qua.

dotnet test tests/Berkeley.Networking.Tests/Berkeley.Networking.Tests.csproj --nologo -v minimal
# Networking: 8 đạt; 0 thất bại, 0 bỏ qua.
```

Tổng cộng **46/46 test đạt**. Solution hiện chưa khai báo project Networking.Tests nên chỉ chạy `dotnet test` trên solution sẽ không chạy 8 test mạng này.

Build phát cảnh báo `NU1903` với dependency `SQLitePCLRaw.lib.e_sqlite3` phiên bản `2.1.10` (advisory do NuGet trả về: `GHSA-2m69-gcr7-jv3q`); cần xử lý dependency trước khi phát hành. Chưa cập nhật package trong lần rà soát này.

Chưa mở GUI, chưa chỉnh giờ Windows và chưa chạy thử trên nhiều máy độc lập. Các kết quả trên xác nhận build/test của mã hiện tại, chưa thay thế bằng chứng nghiệm thu thực tế.

## Tài liệu/file nên đọc trước khi làm tiếp

- `../README.md`: cấu trúc và lệnh build cơ bản.
- `../src/Berkeley.Contracts(chung)/Interfaces.cs`, `Models.cs`: hợp đồng chung.
- `../src/Berkeley.Desktop(tv3)/ViewModels/MainViewModel.cs`: điểm ghép module và phần lớn luồng UI.
- `../src/Berkeley.Desktop(tv3)/Configuration/`: cấu hình JSON và preset.
- `../src/Berkeley.Desktop(tv3)/Persistence/SqliteAuditSink.cs`: ghi, đọc và xuất lịch sử.
- `../src/Berkeley.Networking(tv2)/Transport/TcpNetworkTransport.cs`, `Election/BullyLeadershipService.cs`: implementation mạng cần tích hợp.
- `../tests/Berkeley.Networking.Tests/EngineAfterElectionIntegrationTests.cs`: ví dụ ghép Networking với Engine sau bầu lại.

## Cập nhật triển khai trên feature/ui

Xem [kết quả sửa và kiểm chứng TV3](ket-qua-sua-tv3.md). Phần mô tả ở trên là baseline trước khi sửa; trạng thái mới có build Release sạch, 18 test Desktop và 57 test toàn solution đạt. Các phụ thuộc TV1/TV2 vẫn giữ nguyên.
