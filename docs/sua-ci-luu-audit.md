# CI Windows: đầy hàng đợi audit khi chạy sáu node

Hai lượt CI của commit `c29ef09` đều restore và build thành công, nhưng thất bại
ở `SixWindowsClients_RealTcp_TwoCompleteRoundsConvergeAndPersist`:

- [Lượt push](https://github.com/isnika/berkeley-clock-synchronization/actions/runs/44890998893).
- [Lượt Pull Request](https://github.com/isnika/berkeley-clock-synchronization/actions/runs/44891044082).

Log báo `Hàng đợi lưu trữ đã đầy`, sau đó dọn dẹp gặp lỗi `network.db` còn mở.
Engine 54 test, Networking 15 test và Desktop 18 test đều đạt ở hai lượt cũ.
Client đạt 25/26; lỗi xảy ra ở lưu trữ/dọn dẹp test sáu node.

## Nguyên nhân và sửa đổi

Mỗi event trước đây tạo một công việc ghi riêng với một transaction SQLite.
Traffic heartbeat, COORDINATOR và SYNC_STATUS ở sáu node có thể tạo audit nhanh
hơn khả năng commit từng event trên đĩa của runner. Hàng đợi hữu hạn 4096 bị
đầy và lỗi này được báo khi Flush/Dispose.

Writer mới gộp các event audit liên tiếp thành lô tối đa 256 event cho mỗi
transaction. Giữ nguyên giới hạn hàng đợi, mức durability của SQLite và thứ tự
FIFO; các công việc lưu RoundSummary và điểm chờ Flush được thực hiện sau khi
lô đứng trước đã ghi xong. Không bỏ audit hoặc bỏ test để làm CI xanh.

Dispose giải phóng pool kết nối của DB tương ứng sau khi writer kết thúc, kể cả
khi chuẩn bị báo lỗi ghi, để tránh giữ handle SQLite sau khi đóng sink.

## Kiểm chứng

- Test mới ghi một đợt 4000 event, Flush, đối chiếu đủ số lượng/thứ tự và mở DB
  với FileShare.None sau Dispose để kiểm tra handle đã được giải phóng.
- Test lỗi SQLite vẫn yêu cầu Flush/Dispose báo IOException và kiểm tra DB
  được giải phóng sau lỗi.
- Giữ nguyên test sáu Client: đủ mẫu, Applied, hai vòng, SQLite và CSV.
  Ca này chuyển từ heartbeat 100 ms sang NodeOperationParams mặc định của sản
  phẩm (heartbeat 2000 ms, timeout 6000 ms, election 5000 ms, sample 3000 ms,
  round 30000 ms). Trên VM dùng chung, cấu hình 100 ms tạo lượng kết nối lớn,
  có thể dẫn đến bầu lại/hủy vòng. Kiểm tra chức năng sáu node dùng timing vận
  hành thật; bài ghi dồn 4000 audit vẫn kiểm tra riêng khả năng lưu trữ.
- Workflow lưu TRX thành artifact `windows-test-results` kể cả khi test thất bại.
  Gói Windows vẫn chỉ được publish sau khi kiểm thử đạt.

Kết quả kiểm thử cục bộ lưu tại `tests/*/TestResults/ci-storage-full.trx`.
Xác nhận cuối trên Windows runner bằng lượt CI của commit sửa lỗi; kết quả
cục bộ không tự thay thế kết quả CI.
