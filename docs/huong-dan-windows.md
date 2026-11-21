# Chạy Berkeley Time Sync trên Windows

Cập nhật 21/11/2026: chỉ phát triển Windows. Giao diện Avalonia màu tối là bản
chính tại `src/Berkeley.Windows`. Mac đã ngưng; WPF tại
`src/Berkeley.Desktop(tv3)` được giữ làm bản cũ để đối chiếu.

## Chạy từ mã nguồn

Cần Windows 10/11 và .NET 10 SDK. Trong PowerShell:

```powershell
cd D:\HTPT\berkeley-clock-synchronization
.\scripts\run.ps1
```

Hoặc chạy trực tiếp, không phụ thuộc script:

```powershell
dotnet run --project .\src\Berkeley.Windows\Berkeley.Windows.csproj -c Release
```

Lần đầu cần truy cập NuGet để tải các gói chưa có trong cache. Có thể mở solution
`BerkeleyTimeSync.sln` trong Visual Studio và đặt **Berkeley.Windows** làm Startup
Project. Không chọn Berkeley.Desktop nếu muốn giao diện mới.

Mặc định cài mới chạy Demo, không đổi giờ Windows. Nếu đã lưu cấu hình mạng thật,
ứng dụng sẽ dùng lại cấu hình đó, kể cả tùy chọn tự khởi động Node.

## Chạy gói EXE trên các máy trong nhóm

```powershell
.\scripts\publish.ps1
```

Gói ZIP nằm trong `artifacts/release`, tên bắt đầu bằng
`BerkeleyTimeSync-Windows-win-x64-`. Giải nén toàn bộ và chạy
`BerkeleyTimeSync.exe`. Gói self-contained cho Windows x64 không cần cài SDK/runtime.
Không chạy EXE trực tiếp trong ZIP. Đây là gói thử nội bộ, chưa có bộ cài/ký mã.

Để chỉnh giờ hệ điều hành thật, nhấp phải EXE → **Run as administrator**.
Nếu chạy từ nguồn, mở PowerShell bằng quyền Administrator rồi chạy lệnh phía trên.
Kiểm tra cả dòng quyền trên giao diện và kết quả Step thực tế: `Applied`,
`PermissionDenied`, `Rejected`, `Failed`. Dòng quyền chỉ đọc token và không thử chỉnh giờ.

## Vận hành giao diện

- Đầu cửa sổ luôn có danh tính, IP/cổng, giờ local/UTC, chế độ Demo/TCP thật,
  Master/phiên, số máy online và quyền chỉnh giờ.
- **Khởi động / Dừng Node** điều khiển dịch vụ; **ĐỒNG BỘ NGAY** chỉ bật tại Master
  đang chạy và không bầu chọn. Worker tự xử lý yêu cầu từ Master.
- **Bầu chọn / Demo Bully**: chạy kịch bản mô phỏng ở Demo; yêu cầu bầu chọn ở TCP
  thật. Nút này không làm máy Master khác bị crash. Để thử failover thật, dừng Node
  hoặc đóng ứng dụng trên máy Master.
- Chỉnh lệch bằng ô số phút hoặc các nút **+2 / −1 / −3 phút**. Chế độ thật có hộp
  xác nhận trước khi đổi giờ Windows. Chỉ tạo lệch trên máy thử và phục hồi giờ chuẩn sau thử.
- **Bảng điều khiển** giữ bốn chỉ số và kết luận/ngưỡng ở phía trên. Ba mục bên dưới
  mở trực tiếp bảng **Cụm máy**, **Mẫu đo t1–t4**, **Nhật ký TCP / audit**. Kéo ngang
  để xem các cột chi tiết; bảng tự dùng phần chiều cao còn lại của cửa sổ.
- Bảng cụm có Node/tên, vai trò, kết nối, IP/cổng, RTT, offset, mức chỉnh, Step gần
  nhất, lần thấy và chi tiết lỗi. Offset 0 của Master là mốc tương đối của phép đo.
  Kết quả Step gần nhất không thay thế kết quả theo từng vòng trong bảng mẫu.
- Bảng mẫu có t1–t4 UTC, RTT, offset, lệnh Step, trạng thái mẫu, Step và ghi chú;
  Master có hàng Step riêng. Worker xem Step của chính mình trong bảng cụm và audit;
  bảng mẫu tổng hợp/lịch sử vòng được tạo ở máy điều phối Master.
- Nhật ký có tìm kiếm, lọc, xóa phần hiển thị và xuất toàn bộ audit CSV. Xóa phần
  hiển thị không xóa DB. **Lịch sử SQLite** có tải lại, 50 vòng gần nhất và xuất
  toàn bộ lịch sử CSV, gồm thời gian bắt đầu/kết thúc, Master/phiên, số mẫu và kết quả.
- **Cấu hình mạng** có Node ID, tên, IP/cổng, peer, chế độ, tự khởi động, heartbeat,
  election/sample/round timeout, tuổi mẫu và ngưỡng spread. Lưu sẽ kiểm tra dữ liệu
  trước khi ghi file và khởi động lại Node. Chờ thao tác hiện tại kết thúc rồi lưu.
- **Nguyên lý & hướng dẫn** giải thích công thức Berkeley và failover Bully.

## Cấu hình hai máy Windows thật trước

1. Dùng cùng bản EXE mới trên cả hai máy; kết nối cùng LAN/Wi-Fi có thể liên lạc.
   Lấy IPv4 thực tế bằng `ipconfig`. Ví dụ A=`192.168.1.101`, B=`192.168.1.102`;
   phải thay bằng IP của các máy của bạn.
2. Trên A, đặt Node ID=`Node1`, IP lắng nghe=`0.0.0.0` hoặc IPv4 của A, cổng=`5000`.
   Bỏ chọn Demo; danh sách peer chỉ có `Node2`, IP B, cổng `5000`.
3. Trên B, đặt Node ID=`Node2`, IP lắng nghe=`0.0.0.0` hoặc IPv4 của B, cổng=`5000`.
   Bỏ chọn Demo; peer chỉ có `Node1`, IP A, cổng `5000`.
4. **LƯU VÀ KHỞI ĐỘNG LẠI** trên từng máy. Mỗi máy chỉ chạy một ứng dụng cho Node
   ID/cổng đó. Không để WPF cũ và bản mới cùng chiếm cổng.
5. Cho phép ứng dụng/cổng TCP qua firewall trên mạng thử. Kiểm tra từ A sang B:

   ```powershell
   Test-NetConnection 192.168.1.102 -Port 5000
   ```

   Thay IP/cổng và kiểm tra cả chiều ngược lại. `127.0.0.1` chỉ trỏ về chính máy;
   `0.0.0.0` là địa chỉ lắng nghe, không phải địa chỉ peer.
6. Chờ **2/2 máy online**, cùng Master và phiên. Với Node1/Node2, Node2 có ưu tiên
   cao hơn và sẽ làm Master. Tại Master nhấn **ĐỒNG BỘ NGAY**.
7. Kiểm tra một mẫu Worker hợp lệ, hai Step `Applied`, có spread đo lại và kết
   luận theo ngưỡng nhóm đã thống nhất. Thử lại vòng thứ hai, dừng/chạy lại Worker,
   thử độ lệch dương và âm. Kiểm soát nguồn đồng bộ giờ tự động của Windows trong
   bài thử để tránh dịch vụ khác chỉnh giờ đồng thời; phục hồi sau thử.

Nút **Nạp mẫu LAN 6 máy** chỉ điền địa chỉ minh họa `192.168.1.101–106:5000`.
Sửa toàn bộ IP theo mạng thật và loại peer không tham gia. Với ba máy, thử dừng
Master rồi đồng bộ dưới Master mới. Với sáu máy, yêu cầu **6/6 online, 5 mẫu Worker
hợp lệ và 6 Step Applied**, kèm spread đo lại. Không suy ra tất cả peer đã đồng bộ
từ một vòng chỉ có một phần máy tham gia. Chỉ dùng trên LAN tin cậy; protocol hiện
chưa có xác thực/mã hóa peer.

AutoSync đã được triển khai cho TCP: bật trong Cấu hình mạng, nhập chu kỳ
(mặc định 30 giây; hợp lệ 1-86400), rồi Lưu và khởi động lại. Cấu hình mới mặc định bật;
bật trên cả hai máy để tự điều phối khi đổi Master. Sau khi peer online xác
nhận cùng Master/phiên và ổn định, Master tự chạy vòng Berkeley, rồi chạy định
kỳ. Nút Đồng bộ ngay vẫn dùng được giữa các vòng. Tắt AutoSync cho các ca đo
thủ công; Demo dùng nút thủ công. [Quy trình và kiểm chứng](tu-dong-dong-bo.md).
Ngưỡng spread mặc định 60 giây cần sửa theo tiêu chí nghiệm thu của nhóm.

Cấu hình cũ đã lưu AutoSync tắt vẫn giữ nguyên; bật lại trong giao diện nếu muốn
chạy tự động. Cấu hình thiếu trường AutoSync dùng mặc định bật.

### Master cũ khởi động lại (R01)

Dùng cùng gói đã sửa R01 trên cả hai máy. Khi Node2 dừng, Node1 tự lên Master
ở phiên mới. Giữ Node1 chạy và khởi động lại Node2: hai máy tự trao đổi phiên,
Node2 bầu lại theo Bully ở phiên lớn hơn rồi cùng thống nhất Node2/phiên mới.
Không cần nhấn Bầu chọn hoặc dừng cả hai máy. Worker có ưu tiên thấp hơn quay
lại sẽ nhận phiên đang hoạt động, không làm thay Master.

Chờ 2/2 online, bầu chọn kết thúc và **cùng Master/phiên trên cả hai màn hình**
rồi mới Sync. Xác nhận một mẫu Worker, hai Applied và spread đo lại. Nếu sau
30 giây vẫn khác nhau, lưu log/ảnh hai máy và đánh dấu ca chưa đạt; đây là mốc
chẩn đoán, không phải SLA. Bầu chọn có thể đổi phiên hơn một lần. Kiểm tra tự
động đã đạt trên loopback, vẫn cần nghiệm thu LAN vật lý; Bully không bảo đảm
chống hai Master trong lúc mạng bị chia cắt. [Phân tích và kết quả R01](sua-r01-master-tai-gia-nhap.md).

## Cấu hình và dữ liệu cũ

Để giữ dữ liệu đã tạo bằng giao diện trong ảnh, bản Windows tiếp tục dùng:

```text
%LOCALAPPDATA%\BerkeleyTimeSync\CrossPlatform\node_config.json
%LOCALAPPDATA%\BerkeleyTimeSync\CrossPlatform\demo.db
%LOCALAPPDATA%\BerkeleyTimeSync\CrossPlatform\network.db
```

`CrossPlatform` chỉ là tên thư mục dữ liệu lịch sử; không có nghĩa còn phát triển
Mac. Demo và mạng thật có DB riêng. Cấu hình lỗi được giữ nguyên và báo lỗi, không
tự ghi đè bằng mặc định. Đóng ứng dụng sẽ chờ dừng Node và flush SQLite.

WPF cũ dùng `%LOCALAPPDATA%\BerkeleyTimeSync\node_config.json` và
`berkeley_audit.db`. Muốn chuyển **cấu hình WPF** sang giao diện mới: đóng cả hai
ứng dụng, sao lưu hai thư mục dữ liệu, rồi sao chép `node_config.json` từ thư mục
WPF sang thư mục `CrossPlatform`. Kiểm tra Node ID/IP/cổng/chế độ trước khi mở lại;
cấu hình mạng thật có thể tự khởi động. Không đổi tên hay chép đè `berkeley_audit.db`
thành `demo.db`/`network.db` vì lịch sử WPF có thể trộn hai chế độ. Có thể xuất CSV
hoặc mở lại WPF cũ để xem lịch sử đó; việc chuyển cấu hình không nhập lịch sử cũ.

Có thể dùng thư mục riêng cho từng phiên thử, không đụng dữ liệu mặc định:

```powershell
.\scripts\run.ps1 -DataDirectory D:\HTPT\lab\Node1
# Hoặc với gói EXE:
.\BerkeleyTimeSync.exe --data-dir D:\HTPT\lab\Node1
```

Đường dẫn phải tuyệt đối. Chẩn đoán Windows chỉ đọc (không chỉnh giờ):

```powershell
dotnet run --project .\src\Berkeley.Windows -c Release -- --diagnose
```

## Build và kiểm thử

```powershell
dotnet build BerkeleyTimeSync.sln -c Release
dotnet test BerkeleyTimeSync.sln -c Release --no-build
.\scripts\publish.ps1
```

Test mạng dùng TCP thật trên loopback và đồng hồ giả để không chỉnh giờ máy chạy
test. Bao phủ Client mới, bootstrap cũ, cả hai vai trò, độ lệch dương/âm, Step,
lịch sử/CSV, dừng/chạy lại và mất Master. Kiểm tra native Windows trong test chỉ
đọc giờ/token. Các kiểm tra này không thay thế nghiệm thu nhiều máy vật lý.
