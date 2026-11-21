# Kiểm tra giao diện và khả năng tích hợp máy thật — 21/11/2026

Đây là báo cáo trước khi sửa. Ba lỗi bên dưới đã được xử lý trong đợt tiếp theo cùng ngày; xem [kết quả sửa và hướng dẫn thử LAN](san-sang-thu-lan-2026-11-21.md).

## Kết luận

Giao diện đã nối Engine và TCP/Bully thật, có thể tiếp tục thử kết nối LAN có kiểm soát. Chưa sẵn sàng nghiệm thu đồng bộ nhiều máy thật: có ba lỗi Engine tái hiện được. Đợt này chỉ kiểm tra, không sửa mã sản phẩm và không chủ động chỉnh giờ Windows.

## Kiểm chứng hiện tại

- `dotnet build BerkeleyTimeSync.sln -c Release --no-restore`: thành công, 0 warning, 0 error.
- `dotnet test BerkeleyTimeSync.sln -c Release --no-restore`: Engine 31/31, Networking 8/8, Desktop 18/18; tổng 57/57. Các test này chưa bao phủ ba tình huống bên dưới.
- Mở EXE Release: đọc được cây accessibility, đồng hồ cập nhật, vai trò Master, chế độ TCP thật, thông tin quyền token, 1/6 máy online và các peer offline không có số đo. Đóng bằng Alt+F4, xác nhận cửa sổ biến mất.
- Công cụ kiểm tra không chụp được ảnh (`FrameArrived timed out`) và không click được tab (`coordinate input geometry is unavailable`). Đây là giới hạn công cụ; chưa kết luận bố cục/DPI hoặc thao tác tất cả tab đều đúng.
- Cấu hình đang lưu ở `%LOCALAPPDATA%/BerkeleyTimeSync/node_config.json`: mạng thật, tự chạy khi mở; peer Node2–Node6 vẫn là `127.0.0.1:5002–5006`. Dashboard báo lỗi kết nối bị từ chối phù hợp với việc không có peer đang chạy tại đó. Cấu hình này chưa dùng để nối các laptop độc lập.

## Lỗi tái hiện bằng đồng hồ giả

Chương trình chẩn đoán cục bộ: `artifacts/readiness-check/Readiness.csproj` (thư mục artifacts được gitignore). Chạy bằng `dotnet run --project artifacts/readiness-check/Readiness.csproj -c Release`. Không dùng WindowsClock, không đổi giờ hệ điều hành.

1. **P1 — Lệnh mới bị coi là hết hạn khi đồng hồ Worker nhanh hơn Master.** `WorkerSyncHandler.cs:144` lấy UTC Worker trừ UTC Master gửi lệnh. Với Worker nhanh 120000 ms và MaxAgeMs 10000, lệnh vừa nhận trả `Rejected: ADJUST expired: 120000ms > 10000ms`. Cần thiết kế kiểm tra tuổi lệnh phù hợp khi UTC hai máy chưa đồng bộ, gắn phiên/yêu cầu với thời gian monotonic cục bộ.
2. **P1 — Spread sau đồng bộ bỏ Master và có thể báo đạt sai.** `SyncRoundOrchestrator.cs:506–513` chỉ đưa offset Worker vào ComputeSpread. Probe trả một Worker còn lệch 120000 ms và báo Apply thành công: summary là `Success`, spread `0ms`. Đây là phản hồi mô phỏng nhằm kiểm tra khả năng phát hiện sai lệch còn lại, không phải bằng chứng Windows API thất bại. Phải đưa Master (offset 0) vào phép tính, kiểm tra số mẫu/độ phủ, tránh kết luận toàn cụm khi thiếu phản hồi.
3. **P1 — Vòng thất bại chặn lần chạy tiếp theo.** `SyncRoundOrchestrator.cs:138–140` return Failed khi không có mẫu hợp lệ; finally không trả state về Idle. Probe không có TIME_RESPONSE cho kết quả `first=Failed; state=Measuring; second=null (blocked)`. Cần reset trạng thái ở mọi nhánh kết thúc và kiểm tra retry sau timeout/hủy.

Các điểm cần rà thêm qua mã nguồn: kiểm tra hủy sau phase Adjust trước khi Master ApplyStep; Worker cho qua LeaderSessionId null và không kiểm tra SourceBootId; AutoSyncEnabled/AutoSyncIntervalSeconds có trong config nhưng chưa có luồng thực thi tương ứng. Lịch sử demo và mạng thật dùng chung DB nên cần tách dữ liệu khi đo nghiệm thu.

## Điều kiện để xác nhận sẵn sàng

Sửa ba lỗi trên và bổ sung test hồi quy cho từng lỗi; rà nhánh hủy và nhận diện phiên. Sau đó cấu hình Node ID duy nhất, IP LAN thực tế và cổng peer cho từng máy; xác nhận TCP qua firewall cùng quyền chỉnh giờ của tiến trình. Nghiệm thu trên 2 máy độc lập với độ lệch cả dương/âm vài phút, rồi 3 máy có dừng/restart Master và Worker, cuối cùng 6 máy. Thu kết quả Step, đủ mẫu trước/sau cho toàn cụm, log và CSV; kiểm soát Windows Time để tránh can thiệp kết quả và phục hồi nguồn giờ chuẩn sau thử.

Chưa thực hiện các bài thử nhiều máy vật lý trong đợt kiểm tra này. Test TCP loopback và test demo không thay thế nghiệm thu LAN, độ chính xác thực tế hay failover trên máy độc lập.
