# Hướng dẫn team kiểm thử: chạy và kết nối Berkeley Time Sync

Mục tiêu trước tiên: chạy đúng ứng dụng trên **hai máy Windows**, kết nối được
hai chiều và thực hiện được một vòng đồng bộ. Làm theo thứ tự dưới đây trước
khi thử nhiều máy hoặc các tình huống mất kết nối.

## 1. Chuẩn bị bắt buộc

- Hai máy Windows 10/11 x64, cùng mạng LAN hoặc Wi-Fi cho phép các thiết bị liên lạc.
  Ưu tiên cùng switch/router; tránh Wi-Fi khách có cô lập thiết bị.
- Cài **.NET 10 SDK** trên cả hai máy; kiểm tra bằng `dotnet --list-sdks`.
- Dùng **cùng một phiên bản mã nguồn** trên cả hai máy, có R01 và AutoSync.
  Lần đầu cần mạng để tải dependency NuGet chưa có trong cache.
- Có quyền Administrator để ứng dụng chỉnh giờ Windows thật.
- Dùng máy dành cho thử nghiệm; lưu công việc trước khi chạy. Một vòng Berkeley
  có thể thay đổi giờ của **cả Master lẫn Worker**.
- Mỗi máy chỉ chạy một bản Berkeley cho Node ID/cổng đã chọn.

Trong bài này, gọi máy A là **Node1**, máy B là **Node2**. Node2 có ưu tiên cao
hơn và sẽ làm Master khi cả hai hoạt động ổn định. Node1 là Worker.

## 2. Chạy mã nguồn bằng PowerShell Administrator

Trên mỗi máy, tìm PowerShell trong Start → chọn **Run as administrator**.
Ví dụ mã nguồn nằm tại `D:\HTPT\berkeley-clock-synchronization`; nếu máy bạn
đặt repo ở chỗ khác thì thay đường dẫn `Set-Location` cho đúng. Thư mục repo
phải có `BerkeleyTimeSync.sln` và thư mục `src`.

### Máy A — chạy với thư mục dữ liệu Node1

```powershell
Set-Location D:\HTPT\berkeley-clock-synchronization
dotnet --list-sdks
New-Item -ItemType Directory -Force C:\BerkeleyLab\Node1 | Out-Null
dotnet run --project ".\src\Berkeley.Windows\Berkeley.Windows.csproj" -c Release -- --data-dir "C:\BerkeleyLab\Node1"
```

### Máy B — chạy với thư mục dữ liệu Node2

```powershell
Set-Location D:\HTPT\berkeley-clock-synchronization
dotnet --list-sdks
New-Item -ItemType Directory -Force C:\BerkeleyLab\Node2 | Out-Null
dotnet run --project ".\src\Berkeley.Windows\Berkeley.Windows.csproj" -c Release -- --data-dir "C:\BerkeleyLab\Node2"
```

`dotnet --list-sdks` cần có dòng SDK `10.x`. Nếu chưa có, cài .NET 10 SDK rồi
mở lại PowerShell. `dotnet run` sẽ build và chạy **Berkeley.Windows**, giao diện
hiện hành. Đợi cửa sổ ứng dụng mở; giữ terminal chạy trong lúc dùng ứng dụng.
Đóng ứng dụng trước khi cập nhật mã nguồn hoặc chạy build lại, để tránh lỗi DLL
đang bị sử dụng. Không mở thêm phiên ứng dụng cùng Node ID/cổng trên một máy.

Ý nghĩa lệnh chạy:

| Phần lệnh | Ý nghĩa |
|---|---|
| `dotnet run` | Build và chạy mã nguồn |
| `--project ...Berkeley.Windows.csproj` | Chọn project giao diện Windows |
| `-c Release` | Dùng cấu hình build Release |
| `--` | Phần phía sau được chuyển cho ứng dụng |
| `--data-dir "C:\BerkeleyLab\Node1"` | Dùng thư mục dữ liệu riêng cho lần chạy này |

### Thư mục riêng chứa những gì?

Ví dụ trên A, ứng dụng quản lý dữ liệu như sau; B tương tự trong thư mục Node2:

```text
C:\BerkeleyLab\Node1\
  node_config.json     Cấu hình Node ID, IP/cổng, peer, AutoSync, timeout...
  network.db           SQLite lưu lịch sử vòng và nhật ký audit khi chạy TCP thật
  demo.db              SQLite cho chế độ Demo, tách với dữ liệu mạng thật
```

**Có cả cấu hình và log ứng dụng**: log gửi/nhận, bầu chọn, chỉnh giờ... được
lưu dưới dạng **audit trong SQLite**, không phải mặc định tạo một file `.log`
riêng. DB tương ứng xuất hiện khi chạy chế độ đó; khi DB đang mở có thể có file
SQLite phụ đi kèm. CSV chỉ được tạo khi chọn xuất và nằm tại đường dẫn bạn chọn.
Thông báo build/lỗi in trong PowerShell không tự được lưu thành file trong thư
mục này.

Ổ C chỉ là cách bố trí thư mục cho bài lab. Có thể thay bằng đường dẫn tuyệt
đối khác như `D:\BerkeleyLab\Node1`. Mã nguồn và dữ liệu có thể nằm ở hai ổ khác
nhau. Thư mục Node1 **không tự đặt Node ID là Node1**; vẫn phải nhập Node ID
trong giao diện ở bước 3.

Luôn chạy lại đúng lệnh với cùng `--data-dir` để dùng lại cấu hình và lịch sử.
Nếu bỏ tham số này, app dùng `%LOCALAPPDATA%\BerkeleyTimeSync\CrossPlatform`,
có thể là một bộ dữ liệu khác. Không cần chép DB hoặc cấu hình A sang B.

Lần đầu dùng thư mục mới, ứng dụng mặc định là **Demo**; số máy online trong
Demo là mô phỏng. Nhấn **Dừng Node** trước khi cấu hình mạng thật. AutoSync mặc
định bật cho cấu hình mới, nhưng chỉ hoạt động với TCP; bước 3 sẽ tạm tắt để
kiểm tra vòng thủ công trước.

## 3. Xác định IP và cấu hình hai máy

### 3.1. Lấy IPv4 thật trên từng máy

Mở một cửa sổ PowerShell khác để tra mạng trong khi terminal đầu đang chạy app:

```powershell
ipconfig
```

Tìm phần **Wireless LAN adapter Wi-Fi** nếu đang dùng Wi-Fi, hoặc **Ethernet
adapter Ethernet** nếu đang cắm dây. Trong phần card đang kết nối, lấy dòng
**IPv4 Address**; ghi cả Subnet Mask và Default Gateway để đối chiếu mạng.
Không lấy địa chỉ card báo `Media disconnected`, VPN, Bluetooth hay card ảo
VMware/VirtualBox/vEthernet.

Có thể đối chiếu card và IPv4 bằng lệnh:

```powershell
Get-NetIPConfiguration
```

Đọc `InterfaceAlias` để xác định card đang dùng, rồi lấy `IPv4Address` tương ứng.
Ví dụ ghi lại kết quả hai máy:

- A: `192.168.1.101`.
- B: `192.168.1.102`.

**Thay hai IP ví dụ bằng IP thực tế.** Không tự đổi IP Windows theo ví dụ.
Nếu DHCP làm IP thay đổi sau lần khởi động lại, phải cập nhật địa chỉ peer.

### 3.2. Quy định Node ID

Người quản trị tự thống nhất và nhập ID trên từng máy; Master không cấp ID.
Trong bài này, A đặt `Node1`, B đặt `Node2`. Không đặt cả hai là `Node1`.
Ứng dụng so sánh ID không phân biệt hoa thường, nên `Node1` và `node1` vẫn là
cùng ID. Vai trò Master/Worker có thể đổi khi bầu chọn, còn Node ID giữ nguyên.

Node2 có ưu tiên cao hơn Node1 theo thứ tự chuỗi, nên việc mở A trước không
buộc A làm Master lâu dài. Khi mở rộng nhóm, nên thống nhất tên Node1–Node6;
không suy ra thứ tự số với tên như Node10.

### 3.3. Nhập IP lắng nghe và danh sách peer

Trong tab **Cấu hình mạng**, nhập:

| Trường | Máy A | Máy B |
|---|---|---|
| Node ID | `Node1` | `Node2` |
| Tên hiển thị | `May A` | `May B` |
| IP lắng nghe | `0.0.0.0` | `0.0.0.0` |
| Cổng TCP | `5000` | `5000` |
| Demo mô phỏng | Bỏ chọn | Bỏ chọn |
| Tự khởi động Node khi mở ứng dụng | Tạm bỏ chọn | Tạm bỏ chọn |
| AutoSync | Tạm tắt | Tạm tắt |
| Peer Node ID | `Node2` | `Node1` |
| Peer Address | IP thực của B | IP thực của A |
| Peer Port | `5000` | `5000` |

Xóa các peer mẫu Node3–Node6; bài hai máy chỉ giữ **một peer đối diện**. Không
thêm chính mình vào danh sách peer. Giữ timeout mặc định trong lần thử đầu.

- **Node ID phía trên** là danh tính của chính máy đang thao tác.
- **IP lắng nghe** là nơi ứng dụng trên chính máy nhận kết nối. Dùng `0.0.0.0`
  để lắng nghe trên các địa chỉ IPv4 cục bộ, hoặc nhập đúng IPv4 của chính máy.
- **Danh sách peer** là các máy khác. Mỗi dòng phải khớp Node ID của máy đối
  diện, IPv4 thực của máy đó và cổng mà máy đó đang lắng nghe.
- Sửa dòng peer `Node2` trên A chỉ sửa thông tin A dùng để tìm B; không đổi
  Node ID của B. Phải cấu hình B trực tiếp trong cửa sổ của B.

Với IP ví dụ ở trên: trên A nhập peer `Node2 / 192.168.1.102 / 5000`; trên B
nhập peer `Node1 / 192.168.1.101 / 5000`. Nếu IP thực khác thì thay tương ứng.

**Worker cũng khai báo các máy còn lại**, vì phải tham gia heartbeat, bầu chọn
và có thể trở thành Master. Khi mở rộng lên ba máy:

| Máy | Node ID của chính máy | Danh sách peer |
|---|---|---|
| A | Node1 | Node2 (IP B/cổng B), Node3 (IP C/cổng C) |
| B | Node2 | Node1 (IP A/cổng A), Node3 (IP C/cổng C) |
| C | Node3 | Node1 (IP A/cổng A), Node2 (IP B/cổng B) |

`0.0.0.0` dùng để lắng nghe, **không dùng làm địa chỉ peer**. `127.0.0.1` chỉ trỏ
về chính máy đang chạy, **không trỏ tới laptop còn lại**. Hai máy khác nhau có
thể cùng dùng cổng 5000.

Chọn **LƯU VÀ KHỞI ĐỘNG LẠI** trên từng máy. Nút này khởi động Node ngay, dù
đã bỏ tùy chọn tự khởi động khi mở app. Đợi cấu hình xong cả hai; nếu muốn bắt
đầu có thứ tự, dừng cả hai Node rồi khởi động B trước, A sau.

## 4. Cho phép kết nối qua firewall và kiểm tra hai chiều

Trên mạng lab tin cậy, kiểm tra profile trong Windows là **Private**. Cho phép
ứng dụng/cổng TCP 5000 trên profile này. Nếu máy do tổ chức quản lý, nhờ quản
trị viên cấp rule phù hợp; không tắt toàn bộ firewall.

Ví dụ tạo rule inbound bằng PowerShell Administrator, thay IP trước khi chạy:

Trên A, cho phép B kết nối vào A:

```powershell
New-NetFirewallRule -Name "BerkeleyLab-A-In5000" -DisplayName "Berkeley Lab A TCP 5000" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5000 -RemoteAddress 192.168.1.102 -Profile Private
```

Trên B, cho phép A kết nối vào B:

```powershell
New-NetFirewallRule -Name "BerkeleyLab-B-In5000" -DisplayName "Berkeley Lab B TCP 5000" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5000 -RemoteAddress 192.168.1.101 -Profile Private
```

Chỉ tạo mỗi rule một lần. Nếu đã có rule phù hợp thì dùng lại. Sau khi Node trên
cả hai máy đã chạy, kiểm tra:

```powershell
# Chạy trên A, dùng IP B
Test-NetConnection 192.168.1.102 -Port 5000

# Chạy trên B, dùng IP A
Test-NetConnection 192.168.1.101 -Port 5000
```

Cả hai chiều phải có `TcpTestSucceeded : True`. Nếu False, kiểm tra Node đã
khởi động chưa, IP/cổng, firewall và mạng có cô lập thiết bị không. TCP True mới
chứng minh mở được cổng; vẫn phải kiểm tra trạng thái ứng dụng ở bước tiếp theo.

## 5. Kiểm tra điều kiện trước khi đồng bộ

Trên **cả hai cửa sổ**, phải thấy:

- Chế độ **TCP THẬT**, không phải Demo.
- **2/2 máy online**.
- Cùng Master **Node2** và **cùng số phiên**; không còn đang bầu chọn.
- Node2 là Master, Node1 là Worker.
- Có quyền chỉnh giờ. Nếu thiếu quyền, đóng app và mở lại từ PowerShell Administrator.

Chuẩn bị nguồn giờ Windows để kết quả không bị dịch vụ khác chỉnh đồng thời:

1. Ghi hoặc chụp trạng thái **Set time automatically**, múi giờ và Windows Time
   trước thử. Hai máy nên dùng cùng múi giờ để dễ quan sát; phép đo dùng UTC.
2. Trong bài thử có kiểm soát, tạm tắt **Set time automatically**. Nếu W32Time
   đang chạy, có thể tạm dừng trên máy lab bằng PowerShell Administrator:

   ```powershell
   Get-Service W32Time
   Stop-Service W32Time
   ```

3. Giữ nguồn đồng bộ giờ khác tạm ngưng trong lúc đo và phục hồi theo trạng thái
   ban đầu sau thử. Không đổi NTP/domain hoặc cấu hình máy được quản lý để vượt chính sách.

**Không đánh dấu đạt chỉ vì thấy 2/2 online.** Nếu hai bên khác Master/phiên,
chưa tiến hành đồng bộ; chờ hội tụ rồi lưu log nếu vẫn không ổn định.

## 6. Chạy vòng đồng bộ cơ bản trước

Giữ AutoSync **tắt trên cả hai máy** để dễ theo dõi từng thao tác.

1. Trên **B – Master Node2**, nhấn **ĐỒNG BỘ NGAY**.
2. Worker A tự nhận yêu cầu và xử lý; không cần bấm nút trên A.
3. Đợi các pha Measuring → Adjusting → Verifying hoàn tất, trở về Sẵn sàng.
4. Trên Master, kiểm tra:
   - Có **một mẫu Worker Node1 hợp lệ**.
   - Có **hai kết quả Step `Applied`**, gồm Node1 và Node2.
   - Có giá trị **spread đo lại** và kết quả vòng; đối chiếu ngưỡng team đã chốt.
   - **Lịch sử SQLite** có vòng vừa chạy.
5. Trên Worker, xem kết quả Step của Node1 trong **Cụm máy** hoặc **Nhật ký TCP / audit**.
6. Chạy thêm một vòng để xác nhận có thể đồng bộ liên tiếp.

`PermissionDenied`, `Rejected`, `Failed` hoặc thiếu mẫu không được coi là đạt.
Ngưỡng spread mặc định 60 giây khá rộng; team phải chốt ngưỡng trước khi dùng
để kết luận. Hai đồng hồ gần nhau thể hiện hội tụ trong cụm, chưa chứng minh
đúng UTC chuẩn.

Sau khi vòng cơ bản đạt, có thể thử nút **+2 phút** trên Worker A rồi Sync ở B;
tiếp tục thử **−3 phút** tương tự. Đây là thay đổi giờ hệ điều hành thật, phải
ghi số đo trước/sau và chỉ xác nhận trên máy lab.

## 7. Bật AutoSync và thử node trở lại

Chỉ thực hiện sau khi kết nối và vòng thủ công đã đạt.

1. Trên **cả hai máy**, bật **AutoSync · định kỳ và khi node trở lại (TCP)**,
   nhập **30 giây**, rồi **Lưu và khởi động lại**. Phạm vi hợp lệ 1–86400 giây;
   cấu hình mới mặc định AutoSync bật. Cấu hình cũ đã lưu tắt vẫn giữ nguyên;
   cần bật lại nếu muốn dùng. Demo không chạy lịch tự động.
2. Không nhấn Sync. Chờ các máy xác nhận cùng Master/phiên và trạng thái ổn định.
   Master phải tự chạy vòng đầu, rồi tiếp tục định kỳ. Kiểm tra lịch sử có vòng
   mới, một mẫu Worker, hai Applied và spread đo lại.
3. Dừng Node1 rồi chạy lại. Sau khi 2/2 online và cùng Master/phiên, phải tự có
   vòng đồng bộ cho hai máy.
4. Dừng Node2. Đợi Node1 lên Master ở phiên mới; khi chỉ còn một máy, chưa có
   Worker để chứng minh đồng bộ. Chạy lại Node2, giữ Node1 hoạt động. Hai máy
   phải tự thống nhất Node2 ở phiên cao hơn rồi tự đồng bộ, không cần bấm Bầu chọn.
5. Giữa các vòng tự động, nút **ĐỒNG BỘ NGAY** trên Master vẫn dùng được.
   Trong lúc vòng đang chạy, chờ hoàn tất hoặc dùng **Dừng Node** để hủy.

AutoSync chạy trọn vòng **lấy giờ → tính hiệu chỉnh → chỉnh Worker và Master →
đo lại**. Heartbeat chỉ báo máy còn sống. Chu kỳ tính từ cuối vòng tự động
trước; không yêu cầu thời điểm khởi chạy chính xác từng giây. Nếu bất đồng
Master/phiên kéo dài, thu log hai máy; không dùng nút bầu chọn để che kết quả lỗi.

## 8. Lưu bằng chứng và kết thúc buổi thử

- Ghi phiên bản mã nguồn (commit nếu dùng Git), đường dẫn `--data-dir`, IP/cổng,
  Node ID, chu kỳ AutoSync, Master/phiên và thời điểm chạy.
- Chụp hai cửa sổ, lưu Round ID, số mẫu, Step từng node và spread đo lại.
- Xuất **audit CSV ở cả hai máy**, xuất **lịch sử CSV trên các máy đã làm Master**.
- Dừng Node, đóng ứng dụng để dữ liệu được ghi xong. Không xóa DB khi còn chạy.
- Khôi phục **Set time automatically** và W32Time theo trạng thái ban đầu.
  Nếu dịch vụ ban đầu đang chạy, khởi động lại rồi yêu cầu đồng bộ nguồn chuẩn:

  ```powershell
  Start-Service W32Time
  w32tm /resync
  ```

- Kiểm tra giờ và trạng thái nguồn giờ đã phục hồi; nếu resync lỗi thì ghi lại
  và nhờ người phụ trách xử lý. Nếu không dùng lab nữa, chỉ xóa rule firewall
  do buổi thử đã tạo, bằng đúng tên trên từng máy.

### Checklist trước khi báo team sẵn sàng thử

- [ ] Hai máy dùng cùng mã nguồn và .NET 10 SDK, chạy PowerShell Administrator.
- [ ] Chạy đúng project Berkeley.Windows với `--data-dir` riêng và giữ lại đường dẫn này.
- [ ] Đã bỏ Demo, Node ID khác nhau, IP peer đúng, chỉ giữ peer đang tham gia.
- [ ] TCP 5000 thông cả hai chiều.
- [ ] Hai cửa sổ cùng Master/phiên, 2/2 online và kết thúc bầu chọn.
- [ ] Đã kiểm soát nguồn giờ ngoài và ghi trạng thái để phục hồi.
- [ ] Vòng thủ công có một mẫu hợp lệ, hai Applied, spread và lịch sử.
- [ ] Đã thử AutoSync và node trở lại nếu buổi thử có phạm vi này.
- [ ] Đã xuất bằng chứng và khôi phục giờ chuẩn sau thử.

Kiểm thử tự động hiện dùng TCP loopback và clock giả. Hướng dẫn này dành cho
team xác nhận lại hành vi trên máy Windows vật lý; không điền sẵn kết quả đạt.
