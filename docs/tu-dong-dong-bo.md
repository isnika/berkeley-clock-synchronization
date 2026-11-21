# AutoSync - 21/11/2026

## Phân tích và hành vi

Sau khi node trở lại, Master nên chạy một vòng Berkeley cho các node đang tham
gia, gồm chính Master. Vòng hiện tại đã đo t1-t4, tính hiệu chỉnh, Step tại Worker
và Master, rồi đo lại. AutoSync dùng lại đúng luồng này; không phát thêm một đợt
Step ngay sau vòng vừa hoàn tất vì có thể hiệu chỉnh hai lần theo mẫu cũ.

Chỉ online chưa đủ. Mỗi Client TCP phát SYNC_STATUS chứa Master/phiên và trạng
thái bầu chọn, kể cả khi AutoSync tắt. Master chỉ tự chạy khi ít nhất một peer
online và tất cả peer đang online báo cùng Master/phiên, không bầu chọn, báo cáo
còn hạn theo monotonic. Trạng thái đó phải ổn định ít nhất max(250 ms, 2 nhịp
báo cáo). Nhịp báo cáo là heartbeat interval giới hạn trong 100-1000 ms; TCP I/O
có thể làm chậm hơn. BootId và tập peer online nằm trong dấu hiệu nhận diện cụm.

Đổi Master/phiên, thay BootId hay thay tập peer online sẽ tạo một đợt kiểm tra
ổn định mới. Sau khi ổn định, Master chạy một vòng, rồi đợi chu kỳ cấu hình tính
từ lúc kết thúc lần tự động trước bằng monotonic. Không xếp chồng các vòng tự
động/thủ công; không chạy bù dồn các nhịp đã lỡ. Trước khi chạy, kiểm tra lại vai
trò và phiên. Engine vẫn hủy vòng nếu lãnh đạo thay đổi trong khi đang thực hiện.

Lỗi/quyền Step được ghi vào kết quả và lịch sử như vòng thủ công. Lỗi không gây
thử lại liên tục. Dừng Node/đóng ứng dụng hủy lịch và vòng đang chạy, chờ dọn dẹp.
AutoSync không bảo đảm tất cả peer cấu hình đều online: lịch chạy trên các peer
đang tham gia; giao diện và số mẫu/Step vẫn phải được đối chiếu trước nghiệm thu.

## Vận hành

Trong Cấu hình mạng, bật **AutoSync · định kỳ và khi node trở lại (TCP)**, nhập
chu kỳ rồi **Lưu và khởi động lại**. Cấu hình mới mặc định bật, chu kỳ 30 giây;
phạm vi hợp lệ 1-86400 giây. Bật trên tất cả máy muốn tự điều phối khi lên Master.
Cấu hình được lưu qua lần mở lại ứng dụng. AutoSync chỉ chạy TCP, Demo tiếp tục
dùng nút thủ công. Chạy Administrator khi chỉnh giờ Windows thật.

Nếu cấu hình cũ đã lưu `AutoSyncEnabled: false`, ứng dụng giữ lựa chọn đó;
muốn dùng tự động thì bật lại và lưu. File cũ thiếu trường này dùng mặc định bật.

Nút **ĐỒNG BỘ NGAY** vẫn chạy được giữa các vòng tự động. Khi một vòng đang chạy,
giao diện hiển thị Measuring/Adjusting/Verifying và chặn thao tác xung đột; nút
Dừng Node vẫn dùng được. Peer bản cũ thiếu SYNC_STATUS sẽ khiến Master chờ đối
với AutoSync; cập nhật cùng gói trên tất cả máy. Đồng bộ thủ công vẫn có thể dùng.

Để kiểm thử lệch giờ thủ công và thu số liệu trước/sau không bị AutoSync can
thiệp, tắt AutoSync trên các máy trước bài lab. Ca AutoSync riêng: bật hai máy,
quan sát vòng đầu và ít nhất hai vòng định kỳ không bấm Sync; dừng/chạy lại
Worker, rồi Master, xác nhận cùng Master/phiên và tự có vòng đủ một mẫu/hai
Applied. Thu audit/lịch sử hai bên. Khôi phục nguồn giờ chuẩn khi kết thúc thử.

## Kiểm chứng

Kiểm thử TCP loopback với clock giả bao phủ định kỳ, Worker/Master tái gia nhập,
không chạy tự động khi peer chưa xác nhận phiên, nút thủ công, chống chồng vòng,
dừng lịch, lịch sử SQLite, lưu/mở lại cấu hình và chu kỳ không hợp lệ. Giữ các
test R01, 2/3/6 node và Engine. Không chỉnh giờ hệ điều hành máy kiểm thử.
Chưa nghiệm thu trên nhiều máy vật lý; không có quorum chống split-brain.
