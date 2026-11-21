# Đối chiếu chức năng WPF cũ và Windows mới

Giao diện chính: `src/Berkeley.Windows`, Avalonia. Thiết kế tối, đầu trang và bốn
tab chính giữ theo giao diện người dùng chọn. Dự án WPF vẫn được giữ để đối chiếu
và đọc lịch sử cũ. Contracts, Engine, TCP/JSON, heartbeat và Bully dùng chung.

| Chức năng/thông tin WPF cũ | Vị trí trong giao diện Windows mới |
|---|---|
| Node ID, tên, IP/cổng, local/UTC/ngày | Đầu cửa sổ; bảng cụm có thêm tên máy |
| Demo/mạng thật, Master/Worker, phiên, số máy online | Đầu cửa sổ, luôn nhìn thấy |
| Quyền chỉnh giờ | Khối điều khiển; đọc token, không thử chỉnh giờ |
| Khởi động/dừng Node, đồng bộ thủ công | Khối điều khiển; chỉ Master được Sync |
| Chỉnh lệch +2/−1/−3 phút | Nút nhanh và ô số phút; xác nhận khi chỉnh giờ thật |
| Demo crash/Bully và bầu chọn mạng thật | Bầu chọn / Demo Bully; hướng dẫn dừng Master để thử failover |
| Tiến trình và thông báo bầu chọn | Dòng trạng thái Bully, thông báo từng bước, audit |
| Pha vòng, Round ID, tiến trình | Bốn chỉ số và thanh tiến trình trên Dashboard |
| Offset trung bình, spread trước/sau | Dashboard; spread sau gồm Master |
| Kết luận, ngưỡng, thông báo kết quả có thể đóng | Dashboard, chữ/màu và thông báo kết quả |
| Thẻ từng máy: vai trò, kết nối, endpoint, RTT, offset, mức chỉnh, Step, lần thấy | Mục Cụm máy, dạng bảng có màu trạng thái và cột lỗi |
| t1–t4, RTT, offset, Step, mẫu hợp lệ/loại, ghi chú | Mục Mẫu đo, có hàng Step của Master |
| Nhật ký gửi/nhận/Bully/Step/lỗi, lọc, tìm kiếm | Mục Nhật ký TCP / audit |
| Xuất audit CSV, xóa log hiển thị | Mục Nhật ký; không xóa SQLite |
| Node ID/tên/IP/cổng, chọn IP trên máy | Cấu hình mạng |
| Peer thêm/bớt, mẫu LAN 6 máy | Cấu hình mạng; phải thay IP mẫu |
| Heartbeat/election/sample/round timeout, ngưỡng | Cấu hình mạng; có thêm Sample max age |
| Demo, tự khởi động, lưu/khởi động lại | Cấu hình mạng; kiểm tra trước khi ghi/dừng |
| 50 vòng gần nhất, tải lại, xuất toàn bộ CSV | Lịch sử SQLite; có bắt đầu/kết thúc, Master/phiên và số mẫu |
| Công thức t1–t4/Berkeley, nguyên lý Bully | Nguyên lý & hướng dẫn |
| Trạng thái thao tác/lỗi và nơi lưu dữ liệu | Cuối cửa sổ; đường dẫn DB thật đang dùng |

Các bảng trong Dashboard có mục riêng để mở xem trực tiếp và dùng phần chiều cao
còn lại của cửa sổ. Cột rộng có cuộn ngang; cấu hình có cuộn dọc. Trạng thái có chữ
để người vận hành không phải chỉ dựa vào màu. Worker có hướng dẫn thao tác tại Master.

AutoSync không có luồng thực thi trong bản cũ và vẫn chưa được triển khai. Lịch sử
WPF không tự gộp vào DB mới; xem [chuyển cấu hình/dữ liệu](huong-dan-windows.md).
