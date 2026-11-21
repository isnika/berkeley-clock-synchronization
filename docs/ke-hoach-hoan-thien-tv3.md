# Phân tích và kế hoạch hoàn thiện phần TV3

Ngày: **20/11/2026**. Cơ sở: checkout `main`, commit `0eba6d6`.

## Phạm vi theo yêu cầu người dùng

Người dùng là **TV3**. Tập trung vào `src/Berkeley.Desktop(tv3)`, `tests/Berkeley.Desktop.Tests`, bootstrap, tài liệu triển khai, CI và đóng gói. Engine/Networking chỉ được đọc để hiểu hợp đồng và ghi nhận phụ thuộc. **Chưa sửa mã TV1, TV2 hoặc Contracts.**

Đây là bản phân tích và đề xuất cập nhật, chưa phải báo cáo đã triển khai các sửa đổi. Những thay đổi sản phẩm dưới đây đều là công việc còn phải thực hiện.

## Kết luận về phần TV3

Đã có khung chức năng C1–C9 tương đối đầy đủ: cấu hình, dashboard, đồng hồ, log, SQLite, lịch sử và xuất CSV. Điểm thiếu chính là tính trung thực của kết quả, vòng đời dịch vụ, xử lý lỗi và tích hợp đúng module mạng đã bàn giao. C10 chưa có đủ sản phẩm phát hành/CI và bằng chứng chạy máy độc lập.

7 test Desktop hiện có kiểm tra đường đi cơ bản, chưa chứng minh GUI xử lý đúng restart, dữ liệu thiếu, cấu hình lỗi, DB lỗi hoặc đóng ứng dụng. Không nên đánh dấu TV3 hoàn thành chỉ dựa trên build và 7 test này.

## Những trường hợp đã kiểm chứng thêm

Chạy chương trình chẩn đoán cục bộ `artifacts/tv3-review-probe/Tv3ReviewProbe.csproj`, dùng chính các class Desktop hiện tại. Thư mục `artifacts/` được Git bỏ qua. Dữ liệu cấu hình/SQLite thử được tạo trong thư mục output riêng của chương trình. Không khởi tạo MainViewModel, không dùng WindowsClock, không mở listener hoặc chỉnh giờ Windows.

| Trường hợp | Kết quả thực tế |
|---|---|
| Load JSON bị cắt dở | Nội dung hỏng bị ghi đè thành cấu hình mặc định |
| Load JSON `{"Node":null}` | Trả về đối tượng có Node null, không báo cấu hình không hợp lệ |
| Save vào đường dẫn không ghi được vì tên file là một thư mục | Save trả về bình thường; file cấu hình không được tạo |
| Cập nhật NodeCard từ Connected sang Unknown | Hiển thị OFFLINE, không phân biệt chưa xác định với mất kết nối |
| Snapshot mới không có RTT/offset/LastSeen | Card giữ lại các giá trị cũ, không đánh dấu dữ liệu thiếu/cũ |
| AuditEventKind.MessageRejected | Bị xếp vào nhóm “Khác”, không xuất hiện qua bộ lọc “Lỗi” |
| Dùng FakeClock, chỉ Step Master +35.000 ms | Giờ Worker mô phỏng cũng tăng +35.000 ms dù không nhận ADJUST |
| Ghi AuditEvent có Detail rồi đọc lại | Detail không được trả về dù DB có cột lưu dữ liệu này |
| Giả lập lỗi SQLite bằng cách bỏ bảng audit trong DB thử | Sự kiện vẫn được phát cho UI; ghi/đọc không báo lỗi, truy vấn trả danh sách rỗng |

Đây là **9 quan sát chẩn đoán**, không cộng vào số lượng unit test. Các phát hiện khác phía dưới được kết luận qua mã nguồn và cần regression test khi triển khai.

## Các gói cập nhật cần thực hiện

### TV3-01 — Hiển thị kết quả trung thực (ưu tiên 1)

**Bằng chứng:** `ViewModels/MainViewModel.cs:335`, đặc biệt dòng 346–363. Khi `RemeasuredSpreadMs` là null, code thay bằng 0; chuỗi hiển thị dự phòng là `< 10.0 ms (Hội tụ)`. Nếu Outcome là Success, GUI có thể báo đạt ngưỡng dù chưa có phép đo lại.

**Cập nhật:** tách đánh giá kết quả thành hàm thuần/service để test độc lập. Tách trạng thái áp dụng Step khỏi trạng thái kiểm chứng: chưa đo lại, không đủ dữ liệu, đạt ngưỡng, vượt ngưỡng. Chỉ đánh giá ngưỡng với số đo hữu hạn, không âm và đủ ngữ cảnh. Không dùng giá trị giả để điền số đo thiếu. Đổi nhãn 60 giây thành ngưỡng cấu hình hiện tại, tránh khẳng định đó là yêu cầu đã được xác nhận.

**Đạt khi:** Success + spread null không bao giờ báo đạt; PartialSuccess/Cancelled/Failed có thông điệp phù hợp; dữ liệu đo thật và số lượng máy được kiểm chứng được thể hiện rõ theo dữ liệu Contracts cung cấp. Contracts hiện chưa có số mẫu đo lại, nên TV3 phải ghi rõ giới hạn này thay vì tự suy ra.

### TV3-02 — Vòng đời Node và tích hợp mạng TV2 (ưu tiên 1)

**Bằng chứng:** `MainViewModel.cs:179`, `:209`, `:713`; `Views/MainWindow.xaml.cs:8`. Mạng thật hiện dùng hai implementation trong `Desktop/Services/RealNetwork`, dù project Networking đã được tham chiếu. Restart chỉ gọi StopAsync của NodeApplication, chưa Dispose transport/leadership; mỗi lần khởi tạo lại đăng ký thêm AuditEventLogged. Window chưa có luồng shutdown để dừng dịch vụ và flush DB.

**Cập nhật chỉ ở TV3:** tạo lớp bootstrap/runtime sở hữu các dịch vụ. Nhánh mạng thật ghép `Berkeley.Networking.TcpNetworkTransport` và `BullyLeadershipService` qua API hiện có; không sửa code TV2. Khởi động có await và báo lỗi mở cổng. Bổ sung Start/Stop, trạng thái Starting/Running/Stopping/Stopped, command được bật/tắt theo trạng thái. Khi restart/thoát: ngừng nhận command, chờ tác vụ đang chạy theo khả năng API, gỡ sự kiện, dừng/dispose dịch vụ, flush SQLite và dừng timer. Chỉ bỏ implementation mạng riêng của Desktop sau khi kiểm chứng nhánh mới và bỏ hết tham chiếu.

**Đạt khi:** restart nhiều lần trên cùng cổng không lỗi chiếm cổng; mỗi sự kiện chỉ hiện một lần; Stop thực sự dừng liên lạc; đóng ứng dụng không còn task của Desktop hoặc bản ghi đang chờ mà chưa được xử lý/báo lỗi. Hủy Step an toàn còn phụ thuộc TV1, xem phần phụ thuộc; TV3 không tự tuyên bố đã bảo đảm điều đó.

### TV3-03 — Cấu hình hợp lệ và lỗi lưu rõ ràng (ưu tiên 1)

**Bằng chứng:** `Configuration/AppConfigManager.cs:18`, `:39`; `Configuration/AppConfig.cs:106`; `Views/MainWindow.xaml:687`; `MainViewModel.cs:713`.

**Cập nhật:**

- Validator dùng chung cho Load và Save: Node/Peers/OperationParams khác null; Node ID không rỗng và duy nhất, không trùng chính mình; địa chỉ hợp lệ; cổng 1–65535; thời gian chờ dương và quan hệ timeout phù hợp; ngưỡng là số hữu hạn dương.
- ComboBox IP dùng một đối tượng có nhãn và giá trị riêng: hiển thị `0.0.0.0 (Mọi card mạng)` nhưng lưu `0.0.0.0`. Hiện danh sách có nhãn dài trong khi SelectedItem được bind trực tiếp vào chuỗi IP.
- Lỗi nhập số phải chặn Lưu và chỉ đúng ô lỗi. Không lưu giá trị cũ của property trong khi TextBox đang chứa dữ liệu chuyển đổi không hợp lệ. Commit các ô DataGrid đang sửa trước khi validate.
- Load cấu hình hỏng phải báo lỗi và giữ nguyên file để khôi phục; không âm thầm ghi đè. Save dùng file tạm rồi thay thế, trả lỗi cụ thể; chỉ thông báo thành công sau khi lưu và khởi động thực sự thành công.
- Tách đường dẫn cấu hình/DB khỏi thư mục cài đặt, hỗ trợ đường dẫn có thể truyền vào để test; mặc định dùng thư mục dữ liệu có quyền ghi theo người dùng. Giữ các tùy chọn khi Save, không vô tình reset về mặc định.
- `AutoStartOnLaunch`, `AutoSyncEnabled`, `AutoSyncIntervalSeconds` hiện chỉ được khai báo, chưa điều khiển luồng chạy. Thực thi AutoStart; phần AutoSync giữ tắt/không quảng bá là đã hỗ trợ cho tới khi yêu cầu được chốt.

**Đạt khi:** không restart với cấu hình sai; cấu hình hỏng không mất; lưu thất bại không báo thành công; mở lại ứng dụng giữ đúng thiết lập và IP thuần.

### TV3-04 — SQLite có khả năng flush và báo lỗi (ưu tiên 1)

**Bằng chứng:** `Persistence/SqliteAuditSink.cs:28`, `:73`, `:80`, `:195`, `:319`. Audit dùng queue không giới hạn; tổng kết vòng dùng Task.Run riêng; phần lớn lỗi ghi/đọc bị nuốt. UI nhận event trước khi DB commit, nên thấy log không đồng nghĩa dữ liệu đã được lưu.

**Cập nhật:** dùng một luồng ghi có quản lý cho cả audit và tổng kết vòng; có API chờ commit/FlushAsync. DisposeAsync phải chờ toàn bộ dữ liệu đã nhận và báo lỗi nếu không thể ghi. Hàng đợi có giới hạn cùng cơ chế báo quá tải rõ ràng, không chặn việc ghi timestamp mạng và không âm thầm bỏ sự kiện. Phát trạng thái lỗi lưu trữ qua kênh riêng, tránh ghi lỗi DB vào chính sink đang lỗi. UI phân biệt “không có lịch sử” và “không đọc được lịch sử”.

**Đạt khi:** ghi rồi đóng ngay vẫn đọc đủ dữ liệu đã được xác nhận nhận vào; lỗi DB được hiển thị; tải cao không làm queue tăng vô hạn; lịch sử chỉ refresh sau khi bản ghi vòng đã commit. Nếu vẫn phải bỏ dữ liệu khi quá tải, số lượng mất và trạng thái lỗi phải quan sát được.

### TV3-05 — Đồng hồ và mô phỏng nhất quán (ưu tiên 1)

**Bằng chứng:** `MainViewModel.cs:113`, `:617`; `Services/SimulatedClusterTransport.cs:72`. Mặc định giả lập mạng nhưng vẫn dùng WindowsClock. Worker giả được tính từ giờ Master hiện tại cộng drift, nên khi Master Step thì Worker giả cũng nhảy theo. Mô phỏng hiện cố định Node2–Node6; đổi Node ID/cấu hình chưa tạo ra một cụm giả tương ứng.

**Cập nhật:** tách rõ lựa chọn chế độ. Demo dùng clock giả được inject, có badge thời gian mô phỏng và không gọi API chỉnh giờ thật. Mạng thật dùng clock thật, có trạng thái quyền rõ ràng. Mỗi Worker giả có clock độc lập hoặc dựa trên một nguồn monotonic chung cộng offset riêng, không lấy giờ Master đang bị chỉnh làm nền. Nút tạo lệch trong demo chỉnh clock giả; nút tác động Windows chỉ hoạt động ở chế độ mạng thật đã chọn. Kịch bản giả tuân theo danh tính/cấu hình và nhận cancellation khi dừng.

**Đạt khi:** chỉnh riêng Master không thay đổi timestamp Worker giả; demo mặc định không tác động giờ Windows; đóng/restart demo không còn phản hồi cũ cập nhật vào UI mới. Việc kiểm tra quyền WindowsClock của TV1 có side effect là phụ thuộc riêng, chưa sửa ở đây.

### TV3-06 — Dashboard và log phản ánh đúng trạng thái (ưu tiên 2)

**Bằng chứng:** `MainViewModel.cs:250`, `:261`, `:494`; `NodeCardViewModel.cs:95`; `LogItemViewModel.cs:45`. Timer hiện chỉ cập nhật giờ cục bộ. NodeCard gộp Unknown thành Offline, giữ giá trị cũ khi số đo mới null. Các callback dùng Dispatcher.Invoke đồng bộ, có thể giữ luồng phát sự kiện mạng chờ UI. Snapshot không phải lúc nào cũng phát khi đổi phase hoặc trạng thái peer.

**Cập nhật:** hiển thị riêng Unknown/Connected/Disconnected và Stopped/Electing. Số đo thiếu phải xóa hoặc gắn nhãn “mẫu trước”, kèm vòng/thời điểm đo; không ngầm coi số cũ là số mới. Chuyển callback sang cập nhật Dispatcher bất đồng bộ, theo thứ tự và có giới hạn batch; bỏ callback thuộc runtime đã dừng. Làm mới snapshot có kiểm soát hoặc nhận sự kiện theo hợp đồng hiện có, sau khi xử lý phụ thuộc kiểm tra quyền bên TV1. Bổ sung nhóm từ chối và bộ lọc tương ứng. Nút bầu chọn thật phải phản ánh yêu cầu/kết quả thực, không báo “Master đã crash” hoặc “bầu thành công” chỉ vì đã gọi hàm khởi động bầu chọn.

**Đạt khi:** mất peer/đổi vai trò xuất hiện mà không cần bấm Đồng bộ; log gửi/nhận/từ chối/lỗi lọc được; thiếu dữ liệu được nhận biết; UI vẫn dùng được khi log dồn và khi đang chờ mạng. Không chỉ kiểm tra tốc độ hiển thị đồng hồ.

### TV3-07 — Lịch sử và CSV đủ dùng làm bằng chứng (ưu tiên 2)

**Bằng chứng:** `SqliteAuditSink.cs:201`, `:280`; `MainViewModel.cs:768`, `:800`. History chỉ nạp 50 vòng, export chạy trên danh sách này; audit đọc lại bỏ Detail. Số thực CSV được format theo culture, có thể chứa dấu phẩy trên máy cấu hình Việt Nam và làm lệch cột. Một số trường chuỗi chưa được escape đầy đủ.

**Cập nhật:** phân biệt “xuất trang hiện tại” và “xuất toàn bộ/khoảng thời gian”, truy vấn DB phù hợp. Dùng chung bộ ghi CSV với escape mọi trường và CultureInfo.InvariantCulture cho số; timestamp có ngày và offset/UTC rõ ràng. Xuất thêm cấu hình ngưỡng/chế độ chạy, số đo và trạng thái cần thiết để giải thích kết quả, đánh dấu rõ dữ liệu giả. Đọc lại Detail hoặc cung cấp truy vấn raw JSON để xem chi tiết. Flush trước khi xuất và báo lỗi mở/ghi file rõ ràng.

**Đạt khi:** dữ liệu có dấu phẩy, dấu nháy, xuống dòng, tiếng Việt vẫn đọc lại đúng; chạy dưới vi-VN và en-US không đổi số cột; hơn 50 vòng không bị mất khỏi bản xuất “toàn bộ”; số vòng xuất khớp phạm vi đã chọn.

### TV3-08 — Kiểm thử, CI và phát hành (ưu tiên 2, sau các sửa lỗi trên)

**Cập nhật chỉ ở phạm vi hạ tầng/TV3:**

- Tách factory/runtime, store cấu hình, đánh giá kết quả, exporter và thông báo UI để inject dependency; tránh constructor MainViewModel tự làm toàn bộ I/O và khởi động hệ thống.
- Thêm regression test cho TV3-01 đến TV3-07; ưu tiên các lỗi đã tái hiện, không viết test chỉ kiểm tra lại hình thức binding.
- Đưa Networking.Tests vào solution để lệnh test toàn bộ thật sự bao gồm test mạng; đây là thay đổi solution/hạ tầng, không sửa implementation TV2. Trong CI có thể gọi tường minh từng project.
- Chạy CI trên Windows với SDK thống nhất: restore, build Release, test, publish. Kiểm tra và cập nhật dependency SQLite trong Desktop để xử lý cảnh báo NU1903 đã quan sát; kiểm tra lại cảnh báo sau khi chọn phiên bản.
- Script publish self-contained win-x64 và đóng ZIP, cấu hình mẫu 2/3/6 máy, hướng dẫn IP/cổng/firewall/quyền chỉnh giờ, vị trí log/DB và cách đọc kết quả. Không mặc định IP mẫu là IP thật.
- Kiểm tra bản phát hành trong thư mục/máy không có SDK, không dùng cấu hình hoặc DB của môi trường phát triển. Ghi phiên bản build vào bằng chứng.

**Đạt khi:** một chuỗi lệnh tái tạo được bản ZIP; CI bao phủ cả ba bộ test; chạy được trên máy Windows phù hợp không cài SDK; lỗi cấu hình/quyền được giải thích trên GUI; có hướng dẫn demo và log/CSV thật.

## Các phụ thuộc TV1/TV2: xem xét, chưa sửa

Các mục dưới đây đọc được từ mã nguồn, **chưa chạy tái hiện bổ sung trong đợt kiểm tra TV3**. Chúng cần được phối hợp để xác nhận dự án hoàn chỉnh, nhưng không mở rộng phạm vi sửa của TV3 sang hai module còn lại.

| Chủ trì | Vấn đề quan sát | Ảnh hưởng đến nghiệm thu TV3 |
|---|---|---|
| TV1 | `WindowsClock.HasSetTimePermission()` gọi SetSystemTime để kiểm tra; GetSnapshot cũng gọi phương thức này | Việc chỉ mở/làm mới dashboard có thể tác động giờ nếu có quyền; cần API kiểm tra quyền chỉ đọc trước khi polling snapshot thường xuyên |
| TV1 | Worker tính tuổi ADJUST bằng UTC Worker trừ UTC Master | Máy được cố tình chỉnh nhanh vài phút có thể từ chối lệnh vừa gửi; GUI không thể tự sửa kết quả này |
| TV1 | RemeasurePhase tính spread chỉ từ offset Worker, thiếu Master offset 0 | Một Worker lệch Master vẫn có thể cho spread 0; TV3 phải biết giới hạn của số đo đầu vào |
| TV1 | Nhánh không có mẫu hợp lệ trả về trước khi reset state; sau AdjustPhase chưa chặn lại cancellation/leader đổi trước Step Master | Có khả năng kẹt vòng hoặc Step sau khi mất vai trò; vòng đời TV3 cần phối hợp test tình huống này |
| TV1 + TV2 | Chưa thấy HELLO/ProtocolVersion/Boot ID được kiểm tra đầy đủ; từng module tự tạo Boot ID riêng | Restart, phiên cũ và ID trùng cần thống nhất hợp đồng danh tính; không chỉ sửa nhãn hiển thị |
| TV1 | SnapshotChanged chưa gắn đầy đủ với thay đổi phase/membership; RoundSummary chưa mang số lượng/tập mẫu đo lại | Dashboard thời gian thực và chứng nhận đủ máy kiểm chứng cần bổ sung dữ liệu hoặc cơ chế refresh đã thống nhất |

TV3 tiếp tục làm được validator, lưu trữ, exporter, hàm đánh giá dữ liệu thiếu, runtime/factory và test bằng fake trong khi chờ phối hợp. Không được bù sai số đầu vào bằng số giả hoặc tính lại Berkeley trong ViewModel.

## Thứ tự triển khai đề xuất và cổng nghiệm thu

| Đợt | Công việc | Kết quả kiểm tra bắt buộc |
|---|---|---|
| 1 | TV3-01 và phần validate/store của TV3-03 | Không báo đạt khi thiếu số đo; cấu hình sai không được áp dụng; lỗi ghi không bị giấu |
| 2 | TV3-02 và TV3-05 | Demo không chỉnh giờ thật; Start/Stop/restart sạch; nhánh thật dùng module TV2 |
| 3 | TV3-04 | Audit và RoundSummary commit/flush được; lỗi và quá tải có trạng thái rõ |
| 4 | TV3-06 và TV3-07 | Vai trò/kết nối cập nhật đúng; log phản hồi tốt; CSV đầy đủ và đúng locale |
| 5 | TV3-08, thử tích hợp sau khi xử lý phụ thuộc | CI xanh, ZIP tái tạo được, demo 2/3/6 máy có số đo và bằng chứng thật |

Các bước có thể phát triển bằng fake trước. Nghiệm thu toàn dự án chỉ kết luận sau khi các phụ thuộc ảnh hưởng phép đo/Step được xử lý và kiểm tra nhiều máy độc lập.

## Ma trận regression test tối thiểu cho TV3

| Nhóm | Trường hợp cần có |
|---|---|
| Kết quả | Null/NaN/âm; Success vượt ngưỡng; PartialSuccess; hủy; đúng tại biên ngưỡng |
| Cấu hình | JSON hỏng/null; ID trùng/tự trỏ; IP/cổng sai; timeout sai; lỗi ghi; giữ cấu hình cũ khi thao tác thất bại |
| Vòng đời | Start lỗi cổng; Start/Stop lặp; restart cùng cổng; chỉ một subscription; thoát khi còn audit; event trễ từ runtime cũ |
| Mô phỏng | Master Step không kéo Worker; demo không gọi clock thật; đổi cấu hình Node; dừng hủy phản hồi đang chờ |
| SQLite | Read-after-flush; đóng ngay sau ghi round; DB bị khóa/lỗi; queue quá tải có báo cáo; đọc Detail |
| UI trạng thái | Unknown/Offline khác nhau; số đo null không được coi là mới; lỗi/từ chối lọc được; command phù hợp trạng thái |
| CSV | Tiếng Việt/nháy/phẩy/xuống dòng; vi-VN và en-US; hơn 50 vòng; lỗi ghi file; dữ liệu giả có nhãn |
| Phát hành | Build sạch Windows; test đủ project; publish không cần SDK trên máy đích; data path ghi được |

## Trạng thái sau lần phân tích này

- Baseline đã kiểm chứng trong chat: build thành công; Engine 31, Desktop 7, Networking 8 test đạt.
- Chạy lại riêng `dotnet test tests/Berkeley.Desktop.Tests/Berkeley.Desktop.Tests.csproj --no-build --no-restore --nologo -v minimal` sau phân tích: **7/7 đạt, 0 thất bại, 0 bỏ qua**.
- Đợt phân tích bổ sung tập trung TV3 và có 9 quan sát chẩn đoán nêu trên.
- Chưa sửa mã ứng dụng hoặc test chính thức; chỉ bổ sung tài liệu phân tích và công cụ chẩn đoán cục bộ trong thư mục artifacts bị Git bỏ qua.
- Chưa mở GUI để kiểm tra trực quan; chưa chạy Step thật hoặc nghiệm thu nhiều máy. Không coi các tiêu chí “Đạt khi” trong tài liệu là kết quả đã đạt.

## Trạng thái triển khai tiếp theo

Xem [kết quả sửa và kiểm chứng TV3](ket-qua-sua-tv3.md) để phân biệt những thay đổi đã làm và những tiêu chí còn chưa hoàn tất. Bản kế hoạch này được giữ lại như baseline phân tích.
