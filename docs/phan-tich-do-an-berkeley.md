# 📋 Phân Tích Đồ Án: Đồng Bộ Đồng Hồ Berkeley

> [!NOTE] Tài liệu được tổng hợp từ 2 Google Docs của nhóm (tháng 11/2026)

---

## 1. 🎯 Tổng Quan Đồ Án

**Mục tiêu:** Xây dựng ứng dụng Windows đồng bộ đồng hồ hệ điều hành của các máy trong mạng nội bộ bằng **thuật toán Berkeley**.

| Hạng mục | Chi tiết |
|---|---|
| **Quy mô** | 6 máy: 1 Master + 5 Worker |
| **Mạng** | TCP trên LAN/Wi-Fi |
| **Ngôn ngữ** | C# + .NET 10 LTS |
| **GUI** | WPF (XAML, MVVM) |
| **Lưu trữ** | SQLite cục bộ |
| **Nhóm** | 6 người: 3 người code (TV1, TV2, TV3) + 3 người báo cáo |

### Các quyết định kỹ thuật chốt:
- **Đồng hồ thật**: Chỉnh giờ Windows thật (`SetSystemTime`), không dùng đồng hồ ảo
- **Step** (không Slewing): Áp dụng trực tiếp lượng hiệu chỉnh vào giờ hệ thống
- **Berkeley**: Tính trung bình các mẫu đo hợp lệ → gửi lượng hiệu chỉnh cho từng máy
- **Bully**: Thuật toán bầu chọn Master mới khi Master cũ gặp sự cố
- **TCP**: Toàn bộ giao tiếp dùng TCP; danh sách Node được cấu hình trước

---

## 2. 🔬 Thuật Toán Cốt Lõi

### Berkeley Algorithm (Phép đo 4 mốc)
```
t1 = Giờ Master khi gửi yêu cầu
t2 = Giờ Worker khi nhận yêu cầu
t3 = Giờ Worker khi gửi phản hồi
t4 = Giờ Master khi nhận phản hồi
```
- **Offset**: `d_i = ((t2 − t1) + (t3 − t4)) / 2`
- **RTT**: `r_i = (t4 − t1) − (t3 − t2)`
- **Trung bình**: `d_avg = Σ d_i / số_mẫu_hợp_lệ`
- **Hiệu chỉnh máy i**: `a_i = d_avg − d_i`; Master tự áp dụng `a_M = d_avg`

### Bully Algorithm (Bầu chọn Master)
1. Worker phát hiện Master mất liên lạc (timeout heartbeat)
2. Gửi ELECTION tới các Node có ID cao hơn
3. Node cao hơn trả OK và tiếp tục bầu
4. Node không nhận được OK → tự tuyên bố COORDINATOR
5. Master mới hủy vòng cũ và mở vòng đo mới

---

## 3. 🏗️ Kiến Trúc Module

```
BerkeleyTimeSync/
├── src/
│   ├── Berkeley.Contracts/    ← Hợp đồng & dữ liệu chung
│   ├── Berkeley.Engine/       ← TV1 (bạn)
│   │   ├── Synchronization/
│   │   ├── Clocks/
│   │   └── Runtime/
│   ├── Berkeley.Networking/   ← TV2
│   │   ├── Transport/
│   │   ├── Protocol/
│   │   ├── Membership/
│   │   └── Election/
│   └── Berkeley.Desktop/      ← TV3
│       ├── Views/
│       ├── ViewModels/
│       ├── Persistence/
│       ├── Configuration/
│       └── Bootstrap/
├── tests/
└── docs/
```

### Luồng đồng bộ 1 vòng:
```
GUI (TV3) → [Đồng bộ ngay] → TV1 kiểm tra vai trò, mở vòng
                           → TV2 gửi TIME_REQUEST tới các Worker
                           ← TV2 nhận TIME_RESPONSE, trả dữ liệu cho TV1
                           → TV1 tính trung bình, gửi ADJUST qua TV2
                           ← Worker áp dụng Step, gửi ADJUST_RESULT
                           → TV1 đo lại, phát RoundSummary
                           → TV3 hiển thị + lưu lịch sử
```

---

## 4. 👨‍💻 CÔNG VIỆC CỦA TV1 (BẠN) — Berkeley Engine

> [!IMPORTANT] TV1 chịu trách nhiệm **tính đúng của Berkeley** và **toàn bộ vòng đồng bộ**, từ lấy mẫu đến xác nhận sau Step. TV1 là **đầu mối hợp đồng dữ liệu chung**.

### Danh sách đầu việc (A1–A10):

| Mã | Công việc | Đầu ra |
|---|---|---|
| **A1** | Mô hình mẫu đo, quy ước timestamp, RTT, offset | Dữ liệu chung có đơn vị và quy ước dấu |
| **A2** | Ước lượng offset từ 4 mốc t1/t2/t3/t4; kiểm tra mẫu | Hàm tính toán độc lập, có unit test |
| **A3** | Tính trung bình có Master; tính mức chỉnh từng Node | Kết quả đúng cho lượng chỉnh âm và dương |
| **A4** | Đọc giờ, Step và lớp đồng hồ giả (`ISystemClock`) | Interface + implementation Windows |
| **A5** | Quyền hệ thống, lỗi API, đọc lại giờ sau Step | Kết quả áp dụng trung thực |
| **A6** | Thu mẫu → tính → gửi chỉnh → chờ kết quả → đo lại | State machine vòng đồng bộ |
| **A7** | Kiểm tra Master, phiên, vòng, tuổi mẫu, lệnh trùng | Không áp dụng lại hiệu chỉnh đã áp dụng |
| **A8** | Timeout, mất Worker, đổi Master giữa vòng | Kết quả một phần hoặc hủy vòng rõ ràng |
| **A9** | API: khởi động, dừng, đồng bộ và snapshot | Interface `INodeApplication` cho GUI (TV3) |
| **A10** | Unit test và test tích hợp nghiệp vụ | Bộ test + bằng chứng kết quả |

### Ranh giới module (TV1 KHÔNG làm):
- ❌ Không truy cập mã nội bộ của Networking (TV2)
- ❌ Không điều khiển trực tiếp WPF control (TV3)
- ❌ `SetSystemTime` chỉ được gọi trên luồng áp dụng lệnh hợp lệ

### Interface TV1 phải cung cấp:
| Interface | Dùng bởi |
|---|---|
| `ISystemClock` | Bộ điều phối TV1 (đọc + áp dụng Step) |
| `IClockReader` | TV2 tại tầng I/O (chỉ đọc timestamp/monotonic) |
| `INodeApplication` | GUI của TV3 (điều khiển Node) |

### Interface TV1 sử dụng từ người khác:
| Interface | Cung cấp bởi |
|---|---|
| `INetworkTransport` | TV2 |
| `ILeadershipService` | TV2 |
| `IAuditSink` | TV3 (để ghi lịch sử) |

---

## 5. ✅ Tiêu Chí Hoàn Thành của TV1

Phần code của bạn đạt khi:
- [x] Trung bình và dấu (±) tính đúng
- [x] Master cũng tự chỉnh giờ của mình
- [x] Xử lý đúng trường hợp qua nửa đêm và khác ngày
- [x] Không chặn offset vài phút (không dùng ngưỡng 10s)
- [x] Thiếu quyền → không báo APPLIED
- [x] Lệnh trùng → không chỉnh lại lần hai
- [x] Đổi Master → hủy vòng cũ, không tiếp tục vòng cũ
- [x] Phân biệt rõ: kết quả đã áp dụng vs. kết quả đo sai số
- [x] Unit test không thay đổi giờ hệ điều hành (dùng clock giả)

---

## 6. 📦 Tài liệu bàn giao của TV1 cho nhóm báo cáo

- Công thức tính toán Berkeley
- Ví dụ minh họa 6 máy
- Sơ đồ luồng vòng đồng bộ
- Mô tả cách Step hoạt động
- Bộ test và kết quả
- Số đo trước/sau đồng bộ

---

## 7. 🗂️ Các Nhóm Thông Điệp Giao Thức

| Nhóm | Thông điệp | Mục đích |
|---|---|---|
| Nhận diện | HELLO | Node ID, Boot ID, phiên bản |
| Giám sát | PING, PONG | Theo dõi heartbeat |
| Bầu chọn | ELECTION, OK, COORDINATOR | Bully algorithm |
| Đo thời gian | TIME_REQUEST, TIME_RESPONSE | Thu 4 mốc t1-t4 |
| Hiệu chỉnh | ADJUST, ADJUST_RESULT | Áp dụng Step + báo kết quả |
| Quan sát | ROUND_RESULT, CLUSTER_STATUS | Hiển thị GUI |

---

## 8. 🔗 6 Mốc Tích Hợp

| Mốc | Kết quả cần đạt | Ai làm |
|---|---|---|
| 1 | GUI khởi động Node, hiện cấu hình/trạng thái | TV1 + TV3 |
| 2 | Hai máy kết nối, GUI hiện HELLO/PING/PONG | TV2 + TV3 |
| 3 | Trao đổi giờ, hiển thị mức chỉnh dự kiến | Cả 3 |
| 4 | Step thật, phản hồi kết quả, đo lại | Cả 3 |
| 5 | Ba máy bầu Master; dừng Master rồi tiếp tục | TV1+TV2 (TV3 quan sát) |
| 6 | 6 máy đầy đủ, lưu lịch sử, bản phát hành | Cả 3 |

---

## 9. ⚠️ Các Điểm Cần Lưu Ý

> [!WARNING]
> - **Step có thể làm giờ nhảy lùi** — đây là đặc tính chấp nhận được cho demo
> - Không để **lệnh ADJUST cũ** bị áp dụng lại sau khi restart
> - **Mất ACK không có nghĩa Worker chưa chỉnh** → ghi trạng thái "chưa xác định"
> - Bully cơ bản có thể xuất hiện **nhiều Master khi mạng bị phân vùng** — cần ghi nhận giới hạn này

> [!TIP]
> TV1 có thể phát triển hoàn toàn độc lập bằng cách dùng **Transport giả, Clock giả, Leadership giả** cho đến khi TV2 và TV3 sẵn sàng để tích hợp.
