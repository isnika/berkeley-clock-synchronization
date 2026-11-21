# Kết quả sửa và hướng dẫn thử LAN — 21/11/2026

## Trạng thái

Đã sửa ba lỗi tái hiện ở Engine và đạt kiểm tra tự động; có thể bắt đầu thử LAN có kiểm soát trên các máy độc lập. Chưa nghiệm thu độ chính xác hoặc failover trên máy vật lý. Không chủ động chỉnh giờ Windows trong đợt phát triển này.

## Thay đổi

1. Worker không trừ UTC của hai máy để xác định tuổi ADJUST. TIME_REQUEST mở thời hạn cục bộ bằng monotonic, gắn RoundId, LeaderSessionId và SourceBootId của Master. ADJUST chỉ được áp dụng trong thời hạn đó, giới hạn bởi giá trị nhỏ hơn giữa MaxAgeMs và SampleMaxAgeMs cấu hình. Thiếu/sai thông tin vòng đo hoặc phiên, hoặc quá hạn, đều không Step. TIME_REQUEST lặp cùng vòng không gia hạn. SentAtUtcMs giữ lại để chẩn đoán. Đây là giới hạn tuổi từ khi nhận yêu cầu đo, bảo thủ hơn tuổi từ khi Master gửi ADJUST.
2. Spread đo lại bao gồm Master với offset 0. Danh sách Worker cần đo lại lấy từ các mẫu hợp lệ ban đầu, không bỏ Worker vừa ngắt kết nối. Thiếu/bất hợp lệ bất kỳ mẫu đo lại nào trả spread null và PartialSuccess. Mẫu ban đầu bị từ chối cũng khiến vòng PartialSuccess; UI hiện có không đánh dấu đạt cho kết quả này.
3. Finally dọn chờ và trả trạng thái Idle cho cả thất bại, hủy và ngoại lệ. Kiểm tra token sau Adjust ngăn Master tiếp tục Step nếu vòng đã bị hủy; sau đo lại cũng kiểm tra hủy trước khi báo kết quả.

Không đổi cấu trúc JSON; cần dùng cùng bản mới cho mọi máy vì quy tắc kiểm tra ADJUST thay đổi. Chỉ chạy đồng bộ thủ công, AutoSync chưa triển khai. Dữ liệu demo/mạng thật hiện dùng chung DB.

## Kiểm chứng

- Build Release: 0 warning, 0 error.
- Toàn solution: **77/77 đạt** — Engine 49, Networking 10, Desktop 18 (trước sửa 57; thêm 20 trường hợp).
- Test Worker: lệch +120 giây/-180 giây, nhảy giờ UTC nhưng monotonic vẫn còn hạn, lệnh hết hạn theo monotonic, thiếu/sai vòng/BootId/phiên, TIME_REQUEST lặp không gia hạn, lệnh trùng không Step hai lần.
- Test orchestrator: retry sau không có mẫu; spread có Master cho residual dương/âm; mẫu đo lại thiếu/sai/ngắt kết nối; thiếu mẫu ban đầu; hủy ở Adjust/Verifying; retry sau ngoại lệ transport.
- Test TCP thật trên loopback với hai clock giả độc lập: lệch +120 giây và -180 giây đều hội tụ dưới 100 ms trong điều kiện test, đủ kết quả Applied cho hai node và chạy thành công vòng thứ hai. Mốc 100 ms là assert của test local, không phải cam kết độ chính xác LAN.
- Test ba node TCP đã được tăng cường: dừng Node3, hai node còn lại bầu Node2; Node1 ban đầu lệch +120 giây vẫn được Step và đo lại dưới 100 ms. Test dùng đồng hồ giả, không chỉnh giờ hệ điều hành.
- Test Desktop giữ nguyên và vẫn đạt: vòng demo/lưu DB, export CSV, Start/Stop/restart TCP, validation và đọc giờ/quyền token. Chưa xác nhận đầy đủ bằng chuột, bố cục đa DPI hay tải dài hạn.
- Đã publish self-contained win-x64 vào `artifacts/release/BerkeleyTimeSync-20261121-LAN` và đóng ZIP cùng tên, kèm hai tài liệu hướng dẫn. Lần publish thường bị môi trường chặn truy cập NuGet; lần thành công dùng cache NuGet cục bộ, runtime 10.0.7 và tắt truy vấn audit trực tuyến riêng cho lệnh publish (không đổi cấu hình dự án).

Lệnh kiểm tra:

```powershell
dotnet build BerkeleyTimeSync.sln -c Release --no-restore
dotnet test BerkeleyTimeSync.sln -c Release --no-build --no-restore
./scripts/publish.ps1
```

## Thử trên máy thật

1. Dùng cùng gói win-x64 mới trên các máy Windows. Giải nén ZIP rồi chạy EXE. Cấu hình ở `%LOCALAPPDATA%\BerkeleyTimeSync\node_config.json`; cấu hình cũ còn được dùng khi thay EXE.
2. Sao lưu DB và cấu hình trước thử. Chọn mạng thật, đặt NodeId duy nhất. Điền IP LAN hiện tại và cổng của từng peer; `127.0.0.1` chỉ trỏ về chính máy đang chạy. Mẫu 192.168.1.101–106 là địa chỉ minh họa, phải sửa theo IP thực tế.
3. Bảo đảm TCP tới cổng lắng nghe đi qua firewall trên mạng thử. Ví dụ kiểm tra từ máy A: `Test-NetConnection <IP-may-B> -Port 5000` (thay IP/cổng đúng thực tế). Chạy tiến trình có quyền chỉnh giờ; kiểm tra từng kết quả Applied, PermissionDenied hoặc Failed. Badge quyền token không chứng minh Step sẽ thành công.
4. Kiểm soát Windows Time trong thời gian thử để dịch vụ khác không điều chỉnh giờ đồng thời. Thực hiện độ lệch có chủ đích trên máy thử/VM; ghi nguồn giờ cần khôi phục trước khi bắt đầu.
5. Thử hai máy trước: cấu hình đúng một peer trên mỗi máy; xác nhận 2/2 online. Tạo lệch cả dương/âm vài phút, Đồng bộ ngay tại Master. Yêu cầu một mẫu Worker hợp lệ, hai kết quả Applied, có spread sau đo và dưới ngưỡng đã thống nhất. Chạy vòng thứ hai, dừng/khởi động lại Worker và thử lại sau một vòng lỗi.
6. Thử ba máy: xác nhận 3/3, hai mẫu Worker và ba kết quả Applied. Dừng Master, chờ các node còn lại thống nhất Master/phiên mới rồi chạy vòng tiếp theo. Khởi động lại node đã dừng và thử lại.
7. Thử sáu máy: xác nhận 6/6 online trước vòng; yêu cầu năm mẫu Worker hợp lệ và sáu kết quả Applied. Thiếu peer thì không dùng kết quả vòng làm bằng chứng cả sáu máy đã hội tụ.
8. Lưu IP/cổng, phiên Master, log, CSV, số đo trước/sau, kết quả từng Step và trạng thái dịch vụ giờ. Kết thúc thì phục hồi nguồn đồng bộ giờ chuẩn và cấu hình phục vụ sử dụng bình thường.

Ngưỡng mặc định 60 giây phải được đối chiếu tiêu chí nghiệm thu của nhóm; chỉ nhãn đạt trên Dashboard là chưa đủ, cần đủ số máy và số mẫu nêu trên.
