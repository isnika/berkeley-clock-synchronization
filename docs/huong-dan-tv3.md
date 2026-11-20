# Chạy và kiểm chứng Desktop TV3

Bản cập nhật 20/11/2026 trên nhánh feature/ui.

- Chạy EXE từ thư mục đã giải nén. Bản self-contained không cần SDK.
- Cấu hình và DB được lưu ở `%LOCALAPPDATA%\BerkeleyTimeSync`. File cấu hình cũ bên cạnh EXE không được tự nhập; sao lưu rồi chuyển `node_config.json` tới vị trí mới nếu cần. Cấu hình không hợp lệ sẽ báo lỗi và giữ nguyên file.
- Mặc định là demo: dùng đồng hồ mô phỏng độc lập, không chỉnh giờ Windows. Dữ liệu demo và mạng thật hiện cùng kho lịch sử; nên dùng kho dữ liệu sạch riêng cho bài nghiệm thu và lưu bản sao DB trước khi chuyển chế độ.
- Muốn dùng giờ thật: bỏ chọn giả lập, đặt Node ID duy nhất, IP/cổng của các máy thực tế rồi Lưu. Chạy với token có quyền SeSystemtimePrivilege để áp dụng Step. Dashboard kiểm tra quyền bằng cách đọc token, không thử chỉnh giờ.
- Khởi động/Dừng điều khiển Node; Đồng bộ ngay chỉ dành cho Master. Trước khi đổi cấu hình, chờ vòng đồng bộ/bầu chọn hiện tại kết thúc.
- Ngưỡng 60 giây là cấu hình của ứng dụng; chưa thay thế tiêu chí nghiệm thu do nhóm/thầy xác nhận.
- Lịch sử hiển thị 50 vòng gần nhất; xuất CSV lịch sử lấy toàn bộ vòng đã commit. Lỗi lưu/đọc được báo rõ. Đóng cửa sổ chờ dừng Node và flush SQLite.

## Build, test, phát hành

```powershell
dotnet build BerkeleyTimeSync.sln -c Release
dotnet test BerkeleyTimeSync.sln -c Release
./scripts/publish.ps1
```

## Phạm vi kiểm chứng đồng hồ thật

Đã kiểm tra đọc giờ Windows, đọc token và gọi Step 0 ms trên máy phát triển: Applied, đọc lại khớp UTC của Windows. Điều này kiểm chứng đường gọi Windows API, không chứng minh Berkeley hội tụ khi các máy lệch nhau vài phút.

Cần nghiệm thu trên 2 máy độc lập trước, rồi 3 máy với lỗi Master, cuối cùng 6 máy. Thu số đo trước/sau, kết quả Step, log và CSV. Xác định IP thật/cổng TCP/firewall và trạng thái Windows Time trước thử; phục hồi nguồn đồng bộ giờ chuẩn sau thử.

Các phụ thuộc TV1/TV2 đã ghi trong `ke-hoach-hoan-thien-tv3.md` vẫn còn: tuổi ADJUST đang so UTC hai máy lệch nhau; spread đo lại thiếu Master; một số nhánh timeout/hủy chưa hoàn thiện; danh tính phiên và số mẫu đo lại cần xác nhận. Không coi nhãn đạt ngưỡng của Desktop là chứng nhận toàn bộ cụm khi các phụ thuộc này chưa xử lý. TV3 không sửa Engine/Networking trong đợt này.
