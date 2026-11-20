# Kết quả sửa và kiểm chứng TV3 — 20/11/2026

Nhánh: `feature/ui`, đã fast-forward từ `1126efd` tới `0eba6d6` trước khi sửa. Chưa commit/push. Mã Engine, Networking và Contracts không thay đổi so với `0eba6d6`.

## Các thay đổi đã triển khai

- Nhánh mạng thật Desktop dùng TcpNetworkTransport/BullyLeadershipService của TV2.
- Có Start/Stop, chờ vòng đang chạy/bầu chọn, dừng dịch vụ và flush SQLite khi thoát; audit handler chỉ đăng ký một lần.
- DesktopClock đọc giờ Windows thật, kiểm tra privilege qua token không chỉnh giờ; chặn Step khi dừng. Demo dùng clock độc lập, Worker giả không nhảy giờ khi Master Step.
- Số đo null/NaN/âm không được báo đạt ngưỡng. Kết quả chỉ nói về số đo nhận được, không chứng nhận toàn cụm.
- Validate cấu hình, giữ file hỏng, lưu qua file tạm, báo lỗi I/O; IP ComboBox lưu IP thuần, lỗi nhập số chặn Lưu.
- SQLite ghi audit/round qua queue giới hạn 4096, có FlushAsync, báo lỗi/quá tải; đọc lại Detail; export toàn bộ vòng với số invariant và escape CSV.
- Phân biệt Unknown/Offline, xóa số đo khi snapshot thiếu; MessageRejected vào bộ lọc lỗi; callback UI bất đồng bộ và refresh snapshot 500 ms.
- Nâng Microsoft.Data.Sqlite 10.0.0 và SQLitePCLRaw.bundle_e_sqlite3 3.0.2; build không còn cảnh báo NU1903.
- Thêm 11 test Desktop (tổng 18), đưa Networking.Tests vào solution, thêm workflow Windows và script publish ZIP self-contained.

## Bằng chứng kiểm tra

- Build Release: 0 lỗi, 0 warning.
- Engine: 31/31; Networking: 8/8; Desktop: 18/18. Tổng 57 test đạt. Desktop chạy lại sau tinh chỉnh cuối.
- Test Desktop kiểm tra dữ liệu thiếu, JSON hỏng/null, lỗi ghi file, DB lỗi, flush khi đóng, CSV 55 vòng dưới vi-VN, clock demo, token/giờ Windows, Start/Stop/restart TCP thật trên WPF STA, command vòng demo và lưu round.
- Kiểm tra trực tiếp Windows: chênh giữa DesktopClock và DateTimeOffset.UtcNow là 0 ms ở lần đọc; token có SeSystemtimePrivilege; gọi ApplyStep với 0 ms trả Applied; đọc lại chênh 0 ms. Không cố tình đổi giờ máy lệch vài phút.
- Mở cửa sổ Release, đọc được cây accessibility, xác nhận badge demo/đồng hồ/các nút; đóng bằng Alt+F4 và xác nhận cửa sổ biến mất. Screenshot bị lỗi `FrameArrived timed out`; click bị lỗi geometry nên chưa hoàn tất kiểm tra trực quan bằng thao tác chuột. Vòng demo được kiểm tra qua command trong test STA.
- Đã publish self-contained win-x64 và đóng ZIP. CI mới được thêm dưới dạng cấu hình, chưa chạy trên GitHub vì chưa push.

## Giới hạn và công việc còn lại

**Chưa thể xác nhận toàn hệ thống đồng bộ chính xác trên nhiều máy thật.** Giữ nguyên các phụ thuộc TV1/TV2: tuổi ADJUST so hai UTC đang lệch, spread thiếu Master, nhánh hủy/timeout và nhận diện phiên. Step 0 ms thành công chỉ xác nhận quyền/đường gọi API của máy phát triển.

Các phần TV3 cần tiếp tục kiểm chứng: bố cục ở các kích thước/DPI, thao tác nhập sai bằng chuột/bàn phím toàn form, tải log kéo dài, đóng ứng dụng trong lỗi dịch vụ bất thường. Hàng đợi DB giới hạn và báo quá tải nhưng hàng đợi Dispatcher chưa có batching riêng.

Mô phỏng bầu chọn vẫn là kịch bản minh họa, không phải một cụm nhiều Engine đầy đủ; không dùng nó làm bằng chứng failover thật. Lịch sử demo/mạng thật hiện cùng DB và chưa có cột chế độ chạy; cần tách dữ liệu thực nghiệm theo hướng dẫn. AutoSync chưa được thực thi; luồng chính vẫn là Đồng bộ ngay.

Các implementation mạng cũ trong Desktop/Services/RealNetwork được giữ lại nhưng không được bootstrap sử dụng. Không coi tài liệu kế hoạch ban đầu là danh sách đã hoàn tất toàn bộ.

Xem `huong-dan-tv3.md` để chạy và vị trí lưu dữ liệu mới. Cấu hình/DB cũ bên cạnh EXE không tự di chuyển.
