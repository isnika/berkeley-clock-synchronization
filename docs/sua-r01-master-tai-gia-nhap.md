# R01: Master cũ tái gia nhập - 21/11/2026

## Phân tích và chính sách được chọn

Node khởi động lại tạo BullyLeadershipService mới với Master mặc định là Node ID
cao nhất và phiên 1. Node còn sống đã bầu Master ở phiên cao hơn. Heartbeat chỉ
xác nhận kết nối; trước bản vá không có bản tin định kỳ để đối chiếu lãnh đạo.
Vì thế Node1/phiên 2 và Node2/phiên 1 có thể cùng online nhưng không hội tụ.

Giữ chính sách Bully của dự án: node ưu tiên cao hơn khi trở lại sẽ giành Master
qua bầu chọn mới. Node ưu tiên thấp hơn nhận Master/phiên đang hoạt động. Không
gán lại Master chỉ vì một peer chuyển sang online, không lấy UTC làm số phiên.
Thứ tự ưu tiên hiện là so sánh chuỗi không phân biệt hoa thường (Node2 > Node1),
không phải tách phần số trong tên.

Ví dụ: Node2/1 dừng -> Node1/2 -> Node2 trở lại và học phiên 2 -> Node2/3
hoặc cao hơn -> cả hai thống nhất Node2 và cùng phiên. Phiên có thể bỏ qua số
khi có nhiều cuộc bầu chọn; điều kiện là tăng và hội tụ, không cố định phải bằng 3.

## Cơ chế sửa

- Master phát lại COORDINATOR bằng vòng theo dõi hiện có. Nhịp chờ là min giữa
  heartbeat interval và election timeout, tối thiểu 10 ms; thời gian thực còn
  phụ thuộc TCP I/O, timeout và lập lịch. Không mở phiên mới chỉ vì phát lại.
- Node nhận COORDINATOR mới cập nhật Master/phiên; nếu nó có ưu tiên cao hơn
  Master vừa học thì khởi xướng Bully với phiên lớn hơn phiên đó.
- Node trả OK cho ELECTION hợp lệ ghi nhận phiên yêu cầu trước khi tự bầu,
  để node vừa khởi động lại không công bố phiên thấp hơn node còn sống.
- Không nhận phiên thấp hơn phiên lớn nhất đã quan sát. Một ứng viên phải thử
  phiên mới nếu phiên đang bầu bị vượt; không phát COORDINATOR mà chính nó từ chối.
- COORDINATOR trùng không phát LeaderChanged thêm, tránh hủy vòng đang chạy.
  LeaderChanged thật tiếp tục dùng cơ chế Engine hủy vòng/lease của phiên cũ.
- Stop/Dispose hủy và đợi election kết thúc trước khi hủy semaphore; bản tin
  đến khi đang dừng không khởi động một cuộc bầu chọn còn sót lại.

Không thay DTO hay phiên bản giao thức. Cần cập nhật cùng gói mới trên tất cả
máy để có hành vi tái gia nhập thống nhất.

## Kiểm thử hồi quy

Ba biến thể R01 mới đều thất bại trên mã cũ với lỗi hai máy online nhưng khác
Master/phiên. Sau bản vá, kiểm tra Stop/Start và tạo lại ClientSession (mô phỏng
mở lại ứng dụng), gồm một biến thể dùng timeout mặc định 2000/6000/5000 ms.
Hai biến thể nhanh lặp ba chu kỳ, biến thể mặc định chạy một chu kỳ. Mỗi lần
đều tự hội tụ, tăng phiên, Worker không thể điều phối, Master chạy tiếp một vòng
có một mẫu Worker và hai Step Applied; spread đo lại dưới 100 ms trong test.
Worker khởi động lại cũng học phiên hiện tại mà không làm tăng phiên vô ích.

Ca ba Client còn kiểm tra Node3 trở lại sau khi Node2 đã tiếp quản: cả ba cùng
Node3 ở phiên cao hơn, rồi đồng bộ đủ hai mẫu và ba Applied. Giữ kiểm tra sáu
Client, hai vòng đủ năm mẫu/sáu Applied, SQLite/CSV và các kiểm tra Engine/WPF.

Các test Networking mới kiểm tra học phiên từ ELECTION, giành lại từ survivor,
từ chối coordinator cũ, không công bố phiên đã lỗi thời, bỏ mất bản tin đầu
rồi nhận được bản phát lại, và Stop trong lúc bầu chọn. Deadline của ca Engine
sau bầu chọn ba node tăng từ 5 lên 10 giây để chứa thêm một cuộc bầu chọn và
TCP I/O tới Node3 đã dừng; giữ nguyên các assert hội tụ, phiên và Applied.

Kết quả Release: 106/106 đạt (Engine 54, Networking 15, WPF cũ 18, Client 19),
không bỏ qua. Build 0 warning, 0 error. TRX nằm tại tests/*/TestResults/r01-*.trx.
Manifest gói và hash DLL đối chiếu nằm tại artifacts/release/latest-windows.json.

## Nghiệm thu và giới hạn

Test dùng TCP thật trên loopback và clock giả, không Step giờ hệ điều hành.
Chưa chứng nhận trên hai máy Windows vật lý. TC07 phải để máy còn sống chạy,
bật lại Master cũ, không nhấn Bầu chọn, chờ cùng Master/phiên rồi Sync đủ mẫu
và Applied. Ghi riêng thời gian hội tụ, log/CSV và số phiên ở cả hai phía.

Bully không có quorum/fencing chống split-brain trong khi mạng bị chia cắt.
Bản vá khôi phục đồng thuận sau khi liên lạc trở lại; không cam kết một Master
duy nhất suốt thời gian phân vùng. Phiên đang lưu trong RAM: khi toàn bộ cụm
khởi động lại, số phiên có thể bắt đầu lại; không coi đây là epoch bền vững.
BootId và lease monotonic vẫn là các kiểm tra bổ sung của Engine.
