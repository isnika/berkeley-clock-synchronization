# Chuyển giao diện mới thành bản Windows chính — 21/11/2026

## Thay đổi

- `Berkeley.Windows` là ứng dụng Avalonia chính, target `net10.0-windows`, cùng
  giao diện tối mà nhóm đã chọn. Solution, script chạy và script publish trỏ vào bản này.
- Runtime Client chỉ chọn clock thật Windows hoặc demo. Code/test/CI/packaging Mac
  được lưu tại `archive/macos`, ngoài luồng phát triển hiện hành. Tài liệu Mac cũ
  có nhãn lịch sử. CI Windows chỉ tạo gói giao diện mới.
- Giữ Contracts, Berkeley Engine, TCP/JSON, heartbeat, Bully và `WindowsClock`.
  Worker audit bổ sung dữ liệu Step có cấu trúc để giao diện Worker hiển thị kết
  quả của chính nó; không thay đổi giao thức mạng.
- Bổ sung thông tin/chức năng WPF còn thiếu: nút lệch nhanh, trạng thái/tiến trình
  Bully, pha vòng, ngưỡng luôn hiển thị, thông báo kết quả có thể đóng, tên máy,
  chi tiết lỗi Step, thời gian kết thúc lịch sử và hướng dẫn vận hành Worker.
- Dashboard có mục cụm máy/mẫu đo/nhật ký riêng, mở tối đa theo màn hình, bảng dùng
  chiều cao còn lại và có cuộn; hàng dữ liệu không bị xóa/tạo lại theo mỗi nhịp đồng hồ.
- Giữ thư mục dữ liệu Avalonia cũ để bảo toàn cấu hình/lịch sử; demo/network có DB
  riêng. Không tự gộp lịch sử WPF. Xem hướng dẫn chuyển cấu hình trong tài liệu Windows.

## Kiểm tra build

Build Release: **0 warning, 0 error**. Đã tạo gói Windows x64 self-contained bằng
`scripts/publish.ps1` với cache NuGet cục bộ và runtime 10.0.7. Các tham số cache/
runtime chỉ dùng cho lệnh đóng gói tại máy phát triển, không ghim phiên bản toàn repo.

## Kiểm tra chức năng và giao tiếp

**92 test đạt, 0 thất bại, 0 bỏ qua** trên các bộ test hiện hành:

| Bộ test | Đạt |
|---|---:|
| Engine | 49 |
| Networking | 10 |
| WPF cũ | 18 |
| Client Windows | 15 |

Client Windows kiểm tra TCP thật trên loopback qua hai ViewModel giống giao diện
chính, cả độ lệch +120/-180 giây: kết nối, cùng Master/phiên, quyền Sync theo vai
trò, Applied ở Master/Worker, lịch sử/CSV và dừng/chạy lại Worker. Ba Client kiểm
tra mất Node3, bầu Node2 và tiếp tục đồng bộ. Bootstrap giao diện mới ↔ bootstrap
cũ vẫn được kiểm tra theo cả hai vai trò. Các clock trong bài đồng bộ được giả
lập; không chỉnh giờ hệ điều hành máy chạy test.

Giữ test clock demo độc lập, chặn Step sau Stop, Windows factory và đọc token
thật, cấu hình lỗi không ghi đè, hủy khi đóng và flush SQLite. Bổ sung kiểm tra
trạng thái Demo Bully và khả năng hủy/dừng/chạy lại.

Một test failover cũ có cửa sổ heartbeat 180 ms/sample 100 ms bị timeout khi chạy
cùng các assembly khác, nhưng đạt khi chạy riêng. Đã sửa điều kiện chờ để yêu cầu
bầu chọn kết thúc và Node3 ngắt kết nối, bảo đảm cổng khác nhau, tăng thời hạn của
riêng test để chịu được độ trễ lập lịch. Giữ các assert hội tụ và Applied.

## Kiểm tra giao diện Windows

Đã mở gói EXE trên Windows, kiểm tra bố cục và các tab cấu hình, lịch sử, nguyên lý,
cụm máy, mẫu đo và nhật ký. Nhấn Đồng bộ ngay: quan sát pha Measuring rồi Sẵn sàng,
Success, 5 mẫu Worker hợp lệ, 6 Step Applied và lịch sử có vòng tương ứng. Nhãn và
hướng dẫn hiện chỉ nói về Windows. Kiểm tra này dùng Demo, không phải mạng LAN đa máy.

`--diagnose` trả thông tin Windows x64, UTC, đường dẫn dữ liệu và quyền token mà
không chỉnh giờ. Phiên chạy qua terminal kiểm tra hiện không có quyền chỉnh giờ;
muốn Step thật phải chạy phiên có `SeSystemtimePrivilege` theo hướng dẫn.

## Cần nghiệm thu tại nhóm

Chưa chứng nhận hội tụ trên nhiều máy Windows vật lý. Thử hai máy thật trước,
rồi ba máy có mất Master, cuối cùng sáu máy; xác nhận đầy đủ online/mẫu/Step,
spread đo lại, firewall, quyền hệ điều hành và phục hồi nguồn giờ chuẩn sau thử.
AutoSync chưa triển khai, đồng bộ được kích hoạt thủ công. Cần kiểm tra thêm màn
hình/DPI khác và tải dài hạn. [Hướng dẫn hiện hành](huong-dan-windows.md).
