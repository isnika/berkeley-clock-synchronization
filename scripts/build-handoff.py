from pathlib import Path
from html import escape
import json
from reportlab.pdfgen import canvas
from reportlab.platypus import BaseDocTemplate, PageTemplate, Frame, Paragraph, Spacer, PageBreak, Table, TableStyle, Preformatted
from reportlab.lib import colors
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.enums import TA_LEFT, TA_CENTER
from reportlab.lib.pagesizes import A4
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

ROOT = Path(__file__).resolve().parents[1]
RELEASE = json.loads((ROOT / 'artifacts/release/latest-windows.json').read_text(encoding='utf-8-sig'))
ZIP_NAME = Path(RELEASE['package']).name
ZIP_HASH = RELEASE['sha256']
TEST_COUNT = sum(RELEASE['tests'].values())
OUT = ROOT / 'output' / 'pdf'
OUT.mkdir(parents=True, exist_ok=True)
for n, f in [('Arial','arial.ttf'),('Arial-Bold','arialbd.ttf'),('Arial-Italic','ariali.ttf'),('Arial-BoldItalic','arialbi.ttf'),('Mono','consola.ttf')]:
    pdfmetrics.registerFont(TTFont(n, str(Path('C:/Windows/Fonts') / f)))
pdfmetrics.registerFontFamily('Arial',normal='Arial',bold='Arial-Bold',italic='Arial-Italic',boldItalic='Arial-BoldItalic')
W,H = A4
WIDTH = W - 100
styles = {
 'body': ParagraphStyle('body',fontName='Arial',fontSize=10.3,leading=14.5,spaceAfter=7,textColor=colors.HexColor('#18202b')),
 'small': ParagraphStyle('small',fontName='Arial',fontSize=8.8,leading=12,spaceAfter=6,textColor=colors.HexColor('#475569')),
 'h1': ParagraphStyle('h1',fontName='Arial-Bold',fontSize=18,leading=23,spaceAfter=14,textColor=colors.black,keepWithNext=True),
 'h2': ParagraphStyle('h2',fontName='Arial-Bold',fontSize=11.4,leading=15.2,spaceBefore=8,spaceAfter=6,textColor=colors.black,keepWithNext=True),
 'title': ParagraphStyle('title',fontName='Arial-Bold',fontSize=24,leading=29,spaceAfter=13,textColor=colors.black),
 'cell': ParagraphStyle('cell',fontName='Arial',fontSize=9.1,leading=12.6,spaceAfter=0),
 'headcell': ParagraphStyle('headcell',fontName='Arial-Bold',fontSize=9.2,leading=12.6,textColor=colors.white),
 'code': ParagraphStyle('code',fontName='Mono',fontSize=8.8,leading=12.2,leftIndent=9,spaceBefore=3,spaceAfter=9),
}

def p(text, style='body'): return Paragraph(text, styles[style])
def h(text): return p(text,'h2')
def code(text): return Preformatted(text.strip('\n'), styles['code'])
def bullet(text): return Paragraph('• ' + text, styles['body'])
def table(headers, rows, widths=None):
    widths = [WIDTH*x for x in (widths or [1/len(headers)]*len(headers))]
    data=[[p(escape(x),'headcell') for x in headers]]+[[p(str(x),'cell') for x in row] for row in rows]
    t=Table(data,colWidths=widths,repeatRows=1,hAlign='LEFT')
    t.setStyle(TableStyle([
      ('BACKGROUND',(0,0),(-1,0),colors.HexColor('#24374b')),
      ('ROWBACKGROUNDS',(0,1),(-1,-1),[colors.white,colors.HexColor('#f1f5f9')]),
      ('GRID',(0,0),(-1,-1),0.45,colors.HexColor('#d9d9d9')),
      ('VALIGN',(0,0),(-1,-1),'MIDDLE'),('LEFTPADDING',(0,0),(-1,-1),8),
      ('RIGHTPADDING',(0,0),(-1,-1),8),('TOPPADDING',(0,0),(-1,-1),6),('BOTTOMPADDING',(0,0),(-1,-1),6),
    ]))
    return [t,Spacer(1,9)]

class ManualDoc(BaseDocTemplate):
    def __init__(self,path,label):
        super().__init__(str(path),pagesize=A4,leftMargin=50,rightMargin=50,topMargin=58,bottomMargin=46,
            title=label,author='Nhóm dự án Berkeley Time Sync',subject='Bàn giao Windows LAN - bản vá R01 trên nền 0fa3a37')
        self.label=label
        self.starts=[]
        f=Frame(50,46,WIDTH,H-104,id='body',leftPadding=0,rightPadding=0,topPadding=0,bottomPadding=0)
        self.addPageTemplates(PageTemplate(id='manual',frames=[f],onPage=self.decorate))
    def decorate(self,c,doc):
        c.setFont('Arial',8)
        c.setFillColor(colors.HexColor('#64748b'))
        c.drawString(50,H-31,self.label)
        c.drawRightString(W-50,H-31,'WINDOWS  |  BÀN GIAO 21/11/2026')
        c.setFont('Arial',8)
        c.drawString(50,27,'Berkeley Time Sync  |  R01 + AutoSync  |  Phiên bản tài liệu 1.2')
        c.drawRightString(W-50,27,f'Trang {doc.page}')
    def afterFlowable(self,flowable):
        key=getattr(flowable,'bookmark_key',None)
        if key:
            self.canv.bookmarkPage(key)
            self.canv.addOutlineEntry(flowable.getPlainText(),key,level=0,closed=False)
            self.starts.append((key,self.page))

def build(filename,label,pages,prefix):
    story=[]
    for i,(title,items) in enumerate(pages,1):
        if i>1: story.append(PageBreak())
        heading=p(title,'title' if i==1 else 'h1')
        heading.bookmark_key=f'{prefix}{i}'
        story.extend([heading]+items)
    doc=ManualDoc(OUT/filename,label)
    doc.build(story)
    expected=list(range(1,len(pages)+1))
    actual=[n for _,n in doc.starts]
    print(filename,doc.page,'pages; section starts',actual)
    return {'file':filename,'pages':doc.page,'section_starts':doc.starts,'expected_starts':expected}

MS = [
 ('MS1','Thiết lập IPv4 và DHCP','https://support.microsoft.com/en-gb/windows/experience/connectivity-networking/essential-network-settings-and-tasks-in-windows'),
 ('MS2','New-NetFirewallRule','https://learn.microsoft.com/en-us/powershell/module/netsecurity/new-netfirewallrule?view=windowsserver2025-ps'),
 ('MS3','Test-NetConnection','https://learn.microsoft.com/en-us/powershell/module/nettcpip/test-netconnection?view=windowsserver2025-ps'),
 ('MS4','W32Time và w32tm','https://learn.microsoft.com/en-us/windows-server/networking/windows-time-service/Windows-Time-Service-Tools-and-Settings?tabs=config'),
 ('MS5','Thiết lập ngày giờ Windows','https://support.microsoft.com/en-us/windows/experience/personalization/set-time-date-and-time-zone-settings-in-windows'),
 ('MS6','Set-NetConnectionProfile','https://learn.microsoft.com/en-us/powershell/module/netconnection/set-netconnectionprofile?view=windowsserver2025-ps'),
]
def refs(): return [p(f'[{key}] <link href="{escape(url,quote=True)}" color="#174c79">Microsoft - {title}</link>','small') for key,title,url in MS]

ops=[]
ops.append(('Vận hành và kiểm thử\nhai máy Windows qua LAN',[
 p('<b>Dành cho team kiểm thử máy thật.</b> Tài liệu hướng dẫn chuẩn bị hai máy, chạy giao diện Windows mới, đồng bộ giờ hệ điều hành, thử mất Master, thu bằng chứng và khôi phục máy sau thử.'),
 p(f'Bản bàn giao là <b>gói thử nghiệm đã vá R01</b>, trên nền commit <b>0fa3a37</b>. {TEST_COUNT} kiểm thử tự động đã đạt, gồm Master cũ quay lại và tự thống nhất Master/phiên. Chưa thay thế nghiệm thu LAN vật lý; xem ca TC07 ở trang 10.'),
 *table(['Máy A','Máy B'],[['Node1, Worker lúc hai máy ổn định','Node2, Master lúc hai máy ổn định'],['Ví dụ IPv4 192.168.1.101','Ví dụ IPv4 192.168.1.102'],['TCP lắng nghe 5000','TCP lắng nghe 5000']],[.5,.5]),
 p('<b>IP trên chỉ là ví dụ.</b> Thay bằng IPv4 thực tế. Dùng máy lab Windows 10/11 x64 và tài khoản có quyền Administrator. Mỗi máy có một người vận hành; cử một người ghi biên bản và bấm giờ bằng điện thoại.'),
 h('Lộ trình thao tác'),
 *table(['Trang','Nội dung'],[
 ['2','Chuẩn bị LAN và xác định IP'],['3','Giải nén và mở ứng dụng đúng quyền'],['4','Cấu hình hai Node'],['5','Firewall và kiểm tra TCP hai chiều'],['6','Kiểm soát giờ hệ điều hành trước thử'],['7-8','Đồng bộ cơ bản và thử lệch dương âm'],['9-10','Tắt Master và kiểm tra máy quay lại'],['11','Chẩn đoán lỗi tại hiện trường'],['12-13','Khôi phục máy và biên bản bằng chứng']],[.13,.87]),
 p('Đi theo thứ tự, giữ AutoSync tắt trong TC01-08. Chỉ nhấn <b>ĐỒNG BỘ NGAY</b> khi cùng Master/phiên. Các ca AutoSync TC09-12 nằm ở <b>trang 14</b>; không đánh dấu đạt chỉ vì thấy 2/2 online.','small'),
]))

ops.append(('1 Chuẩn bị LAN và xác định IP',[
 p('Ưu tiên cắm hai máy vào cùng switch hoặc cùng router. Nếu dùng Wi-Fi, dùng cùng mạng nội bộ; mạng khách hoặc chế độ cô lập thiết bị có thể chặn hai máy dù đều truy cập Internet. Kết nối TCP của ứng dụng không cần Internet sau khi đã có gói EXE.'),
 h('Ghi IP trên từng máy'),
 p('Mở PowerShell, chạy các lệnh chỉ đọc sau. Chọn card Ethernet/Wi-Fi đang kết nối LAN; không lấy IP của VPN hoặc card ảo.'),
 code('ipconfig\nGet-NetIPConfiguration\nGet-NetConnectionProfile'),
 *table(['Thông tin cần ghi','Ví dụ A','Ví dụ B'],[
 ['IPv4','192.168.1.101','192.168.1.102'],['Subnet mask hoặc prefix','255.255.255.0 hoặc /24','255.255.255.0 hoặc /24'],['Default gateway','192.168.1.1','192.168.1.1'],['NetworkCategory','Private','Private']],[.40,.30,.30]),
 p('Đây là mô hình mẫu một subnet /24. Không tự dùng các địa chỉ này nếu router đang cấp dải khác. Nếu router cấp IP bằng DHCP, giữ DHCP và nhập IP vừa đọc vào cấu hình peer. Ghi lại IP sau mỗi lần đổi mạng hoặc khởi động lại. [MS1]'),
 h('Nếu cần IP cố định trong bài lab'),
 p('Ưu tiên nhờ người quản lý router đặt DHCP reservation. Nếu phải cấu hình thủ công: vào Settings → Network &amp; internet → kết nối đang dùng → IP assignment → Edit → Manual → IPv4. Nhập IP chưa bị sử dụng, đúng subnet/prefix, gateway và DNS của LAN; ghi lại thiết lập cũ để khôi phục. Không đoán gateway/DNS hoặc dùng IP trùng. [MS1]'),
 h('Kiểm tra profile của mạng thử'),
 p('Các rule firewall ở trang 5 áp dụng cho <b>Private</b>. Vào Settings → Network &amp; internet → Wi-Fi hoặc Ethernet → mạng đang kết nối → thuộc tính mạng, chọn Private. Chỉ làm điều này trên mạng lab tin cậy. Mạng DomainAuthenticated hoặc bị chính sách công ty quản lý phải nhờ quản trị viên cấu hình; không chuyển cưỡng bức sang Private. [MS6]'),
 p('<b>Điểm kiểm tra:</b> đã ghi đúng hai IPv4 khác nhau, xác định cùng đường LAN và profile của card đang dùng. Ping có thể hỗ trợ chẩn đoán, nhưng kiểm tra cổng TCP ở trang 5 mới kiểm tra đúng đường ứng dụng.'),
]))

ops.append(('2 Giải nén và mở bản Windows mới',[
 p('Dùng cùng một ZIP trên cả hai máy. Gói Windows x64 self-contained có sẵn runtime. Giữ AutoSync tắt khi làm TC01-08; ca AutoSync riêng ở trang 14. Không chạy EXE trong ZIP hoặc chỉ chép EXE.'),
 code(ZIP_NAME),
 p('Giải nén, rồi đặt toàn bộ nội dung thư mục chứa <b>BerkeleyTimeSync.exe</b> vào <b>C:\\BerkeleyLab\\App</b> trên mỗi máy. Giữ các DLL và thư mục đi kèm. Nếu Windows chặn gói hoặc thiết bị do tổ chức quản lý, chuyển cho người phụ trách xác minh; không tắt cơ chế bảo vệ để chạy.'),
 h('Chạy bằng PowerShell Administrator'),
 p('Tìm PowerShell trong Start, chọn <b>Run as administrator</b>. Tạo thư mục bằng chứng và mở ứng dụng với thư mục dữ liệu riêng. Chạy đúng khối lệnh cho từng máy:'),
 p('<b>Trên máy A</b>'),
 code('New-Item -ItemType Directory -Force C:\\BerkeleyLab\\Evidence\nSet-Location C:\\BerkeleyLab\\App\n.\\BerkeleyTimeSync.exe --data-dir C:\\BerkeleyLab\\Node1'),
 p('<b>Trên máy B</b>'),
 code('New-Item -ItemType Directory -Force C:\\BerkeleyLab\\Evidence\nSet-Location C:\\BerkeleyLab\\App\n.\\BerkeleyTimeSync.exe --data-dir C:\\BerkeleyLab\\Node2'),
 p('Luôn dùng lại các lệnh này ở lần mở sau. Nhấp đôi EXE không có --data-dir sẽ dùng thư mục mặc định khác, dễ làm tưởng mất cấu hình. Cửa sổ cần mở là <b>Berkeley Time Sync</b> với tiêu đề Windows.'),
 h('Kiểm tra lần mở đầu'),
 p('Thư mục dữ liệu mới mặc định chạy Demo. Có thể thấy 6/6 online giả lập; đó chưa phải hai máy LAN. Nhấn <b>Dừng Node</b> rồi chuyển sang cấu hình trang 4. Không chạy thêm WPF cũ hoặc một bản EXE khác cùng Node ID/cổng.'),
 h('Chỉ dành cho người chạy mã nguồn'),
 code('cd D:\\HTPT\\berkeley-clock-synchronization\ndotnet run --project .\\src\\Berkeley.Windows -c Release'),
 p('Cách chạy mã nguồn cần .NET 10 SDK và NuGet khi thiếu thư viện. Team máy thật nên dùng ZIP thống nhất; không tự build các phiên bản khác nhau trong cùng bài thử.','small'),
]))

ops.append(('3 Cấu hình hai Node thật',[
 p('Mở tab <b>Cấu hình mạng</b>. Thay mọi IP ví dụ bằng IP đã ghi ở trang 2. Mỗi máy chỉ khai báo <b>một peer</b> là máy còn lại; bỏ Node3 đến Node6 của mẫu Demo.'),
 *table(['Trường','Máy A','Máy B'],[
 ['Node ID','Node1','Node2'],['Tên hiển thị','May A Node1','May B Node2'],['IP lắng nghe','0.0.0.0','0.0.0.0'],['Cổng TCP','5000','5000'],['Demo mô phỏng','Bỏ chọn','Bỏ chọn'],['Tự khởi động Node khi mở ứng dụng','Bỏ chọn trong bài lab','Bỏ chọn trong bài lab'],['Peer Node ID','Node2','Node1'],['Peer Address','192.168.1.102','192.168.1.101'],['Peer Port','5000','5000']],[.45,.275,.275]),
 p('<b>0.0.0.0</b> chỉ dùng để lắng nghe trên các card mạng. Peer phải là IPv4 của máy kia. <b>127.0.0.1</b> luôn trỏ về chính máy đang chạy, không dùng cho peer trên LAN.'),
 *table(['Tham số','Giá trị cho cả hai máy'],[
 ['Heartbeat interval / timeout','2000 / 6000 ms'],['Election timeout','5000 ms'],['Sample timeout / max age','3000 / 10000 ms'],['Round timeout','30000 ms'],['Ngưỡng spread','Đề nghị 1.0 giây nếu người phụ trách chốt; mặc định ứng dụng là 60 giây']],[.45,.55]),
 p('Ngưỡng 1.0 giây ở đây là <b>đề xuất cho bài lab</b>, chưa phải tiêu chí nghiệm thu chính thức của môn học. Hai team phải chốt và ghi ngưỡng trước khi chạy; dùng dấu chấm thập phân.'),
 h('Lưu theo đúng thứ tự'),
 p('1. Trên từng máy nhấn <b>LƯU VÀ KHỞI ĐỘNG LẠI</b>. Lệnh này vẫn khởi động Node ngay, dù đã bỏ tự khởi động ở lần mở ứng dụng sau.'),
 p('2. Nhấn <b>Dừng Node</b> ngay sau khi lưu trên từng máy. Chuẩn bị xong cả hai máy trước khi vận hành; tránh để Node1 chạy một mình rồi tự lên Master trong lúc còn cấu hình máy B.'),
 p('3. Tạo rule trang 5. Sau đó <b>Khởi động Node2 trên B trước, rồi Node1 trên A</b>. Chờ cả hai cùng Master Node2 và cùng phiên. Nếu chưa thống nhất, dừng cả hai rồi làm lại thứ tự này và ghi nhận sự cố.'),
]))

ops.append(('4 Mở firewall và kiểm tra TCP',[
 p('Chạy PowerShell Administrator. Các rule dưới đây chỉ mở inbound TCP 5000 từ đúng IP peer trên mạng Private. Không tắt toàn bộ firewall. Dùng đúng khối A/B và thay IP ví dụ. Cú pháp backtick ở cuối dòng là ký tự nối lệnh PowerShell. [MS2]'),
 p('<b>Trên máy A</b>'),
 code('New-NetFirewallRule -Name "BerkeleyLab-A-In5000" `\n  -DisplayName "Berkeley Lab A TCP 5000" `\n  -Direction Inbound -Action Allow -Protocol TCP `\n  -LocalPort 5000 -RemoteAddress 192.168.1.102 `\n  -Profile Private'),
 p('<b>Trên máy B</b>'),
 code('New-NetFirewallRule -Name "BerkeleyLab-B-In5000" `\n  -DisplayName "Berkeley Lab B TCP 5000" `\n  -Direction Inbound -Action Allow -Protocol TCP `\n  -LocalPort 5000 -RemoteAddress 192.168.1.101 `\n  -Profile Private'),
 p('Nếu tên rule đã tồn tại từ buổi lab trước, kiểm tra rule và IP peer trước khi dùng lại; không tạo nhiều rule chồng nhau. Nếu đổi IP, cập nhật rule do nhóm quản lý. Máy có chính sách chặn outbound cần quản trị viên cho phép TCP đến IP peer/cổng 5000.'),
 h('Chạy Node và kiểm tra cổng hai chiều'),
 p('Khởi động B trước rồi A như trang 4. Trên mỗi máy xác nhận có tiến trình lắng nghe:'),
 code('Get-NetTCPConnection -LocalPort 5000 -State Listen'),
 p('<b>Từ A đến B</b> và <b>từ B đến A</b>, chạy tương ứng:'),
 code('# Tren A\nTest-NetConnection 192.168.1.102 -Port 5000\n# Tren B\nTest-NetConnection 192.168.1.101 -Port 5000'),
 p('Cần <b>TcpTestSucceeded : True</b> ở cả hai chiều. Ping thất bại riêng lẻ không đủ kết luận TCP bị chặn. True chỉ chứng minh mở kết nối TCP, chưa chứng minh handshake ứng dụng, Master/phiên hay đồng bộ giờ. [MS3]'),
 p('<b>Điểm kiểm tra ứng dụng:</b> cả hai hiển thị TCP THẬT, 2/2 máy online, một Master Node2 và cùng phiên; A là Worker. Nếu không đạt, dừng ở đây và xem trang 11.'),
]))

ops.append(('5 Chuẩn bị đồng hồ hệ điều hành',[
 p('Berkeley sẽ thay đổi <b>giờ Windows thật</b> của cả Worker và Master. Thực hiện trên máy lab; lưu công việc và tránh lịch hẹn/tác vụ quan trọng trong thời gian thử. Với máy domain hoặc bị quản lý chính sách thời gian, nhờ quản trị viên chuẩn bị môi trường thử.'),
 h('Ghi trạng thái trước khi thay đổi'),
 p('Trên cả hai máy, chụp màn hình Settings → Time &amp; language → Date &amp; time: Set time automatically, múi giờ và trạng thái ban đầu. Ghi thời gian tham chiếu đang dùng. Đọc thông tin trong PowerShell Administrator trên từng máy:'),
 code('Get-Service W32Time | Format-List Status,StartType\nw32tm /query /source\nw32tm /query /configuration\nGet-Date -Format o\n(Get-Date).ToUniversalTime().ToString("o")'),
 p('Lưu đầu ra hoặc chụp màn hình vào C:\\BerkeleyLab\\Evidence. Nếu W32Time đang dừng, w32tm có thể báo lỗi; ghi trạng thái đó, không tự thay nguồn giờ NTP/domain. [MS4]'),
 h('Tạm kiểm soát nguồn đồng bộ khác'),
 p('1. Cho hai máy về giờ chuẩn trước thử bằng nguồn được nhóm sử dụng. Sau đó tạm tắt <b>Set time automatically</b> trong Date &amp; time. Giữ nguyên múi giờ đã chọn. [MS5]'),
 p('2. Dừng W32Time trong thời gian đo có kiểm soát và xác nhận trạng thái Stopped:'),
 code('Stop-Service -Name W32Time\nGet-Service -Name W32Time'),
 p('3. Không đổi StartupType, không unregister dịch vụ và không thay NTP server. Kiểm tra không có công cụ đồng bộ giờ khác. Nếu giờ tự nhảy về trong lúc thử, chưa thể dùng kết quả để kết luận Berkeley hội tụ.'),
 h('Xác nhận quyền của ứng dụng'),
 p('Giao diện TCP thật phải báo có quyền chỉnh giờ. Nếu thiếu quyền: đóng ứng dụng, mở lại bằng PowerShell Administrator với đúng --data-dir, rồi Khởi động B trước A. Quyền trên giao diện chỉ đọc token; <b>Step Applied</b> mới xác nhận lệnh hiệu chỉnh đã được Windows chấp nhận.'),
 p('Dùng UTC hoặc cùng múi giờ để đối chiếu. Đồng bộ Berkeley làm các máy gần nhau, không tự bảo đảm chúng đúng UTC. Sau bài thử phải thực hiện quy trình khôi phục trang 12.'),
]))

ops.append(('6 Kiểm thử đồng bộ cơ bản',[
 p('<b>TC01 - Kết nối và vai trò.</b> Chụp cả hai cửa sổ trước đồng bộ: TCP THẬT, Node1/Node2 đúng IP/cổng, 2/2 online, cùng Master Node2 và cùng phiên, quyền chỉnh giờ hợp lệ. Node1 không có quyền kích hoạt vòng; nút Sync chỉ dùng trên Master đang sẵn sàng.'),
 h('TC02 Chạy một vòng và lặp lại'),
 p('1. Trên B, nhấn <b>ĐỒNG BỘ NGAY</b> một lần. Không đồng thời sửa cấu hình hoặc bầu chọn. Theo dõi Measuring → Adjusting → Verifying → Sẵn sàng. Các pha có thể chuyển nhanh, không yêu cầu chụp đủ từng pha.'),
 p('2. Tại B mở mục <b>Mẫu đo t1-t4</b> của Dashboard. Kiểm tra một mẫu Worker Node1 hợp lệ và hàng Step của Master Node2. Kéo ngang để xem lỗi/ghi chú nếu cần.'),
 p('3. Kiểm tra kết luận, số mẫu, số Step và spread sau đo lại; sau đó mở <b>Lịch sử SQLite</b> để thấy Round ID mới. Tại A xem Step của chính Node1 trong bảng Cụm máy và nhật ký. Bảng tổng hợp vòng chủ yếu nằm ở Master.'),
 *table(['Điều kiện đạt cho mỗi vòng hai máy','Phải quan sát'],[
 ['Thành phần tham gia','2/2 máy online trước vòng'],['Mẫu Worker','1 mẫu hợp lệ; không có mẫu loại'],['Hiệu chỉnh','2 kết quả Step đều Applied'],['Đo lại','Có giá trị spread, không phải -- hoặc null'],['Kết quả và ngưỡng','Success và spread ≤ ngưỡng đã chốt'],['Lưu trữ','Round ID xuất hiện trong lịch sử Master']],[.47,.53]),
 p('4. Khi vòng về Sẵn sàng, chạy vòng thứ hai và kiểm tra lại đủ các điều kiện. Không cần kết quả giống hệt vòng trước; độ trễ mạng và lịch lập lịch có thể làm số đo thay đổi.'),
 h('Đọc đúng trạng thái'),
 *table(['Trạng thái','Cách xử lý'],[
 ['Applied','Lệnh Step đã được áp dụng; vẫn cần đo lại và kiểm tra đủ máy'],['PermissionDenied','Kiểm tra tài khoản/quyền chạy; không đánh dấu đạt'],['Rejected','Xem audit: quá hạn, sai vòng/phiên/BootId hoặc lệnh trùng'],['Failed / PartialSuccess / Cancelled','Ghi nguyên kết quả và log; không đổi ngưỡng để làm xanh kết quả']],[.31,.69]),
 p('<b>Success chưa tự có nghĩa đạt ngưỡng đã chốt.</b> Ngược lại, một spread nhỏ nhưng thiếu máy/thiếu Step cũng chưa đủ bằng chứng cả hai máy đã đồng bộ.'),
]))

ops.append(('7 Kiểm thử lệch dương âm và Worker',[
 h('TC03 Worker nhanh hơn Master khoảng hai phút'),
 p('1. Từ trạng thái cả hai cùng Master/phiên và đã kết nối, trên <b>A Node1</b> nhấn <b>+2 phút</b> hoặc nhập 2 rồi Chỉnh lệch. Đọc hộp xác nhận và chỉ xác nhận trên máy lab. Đây là thay đổi giờ hệ điều hành thật.'),
 p('2. Ghi UTC của A và B ngay sau khi tạo lệch. Có thể dùng PowerShell chỉ đọc: (Get-Date).ToUniversalTime().ToString("o"). Hai lệnh chạy lần lượt có độ trễ; đây là bằng chứng quan sát, không phải phép đo chính xác ở mức ms.'),
 p('3. Trên B nhấn ĐỒNG BỘ NGAY; kiểm tra đủ một mẫu Node1, hai Applied, spread đo lại và lịch sử. Chạy thêm một vòng để kiểm tra hệ thống vẫn vận hành sau Step.'),
 h('TC04 Worker chậm hơn Master khoảng ba phút'),
 p('Khi hai máy đã gần nhau, trên A nhấn <b>-3 phút</b> và xác nhận. Lặp lại các bước ghi trước/sau, đồng bộ tại B và chạy lại vòng thứ hai. Giữ nguyên IP, Node ID, ngưỡng và timeout giữa hai ca để dễ so sánh.'),
 *table(['Ví dụ lý tưởng so với giờ B ngay trước vòng','Worker A','Master B'],[
 ['A đang +120 giây','Step khoảng -60 giây','Step khoảng +60 giây'],['A đang -180 giây','Step khoảng +90 giây','Step khoảng -90 giây']],[.48,.26,.26]),
 p('Đây là minh họa thuật toán, không phải số đo bắt buộc. Master cũng phải chỉnh giờ. Hai máy có thể hội tụ ở một giờ lệch UTC chung; các ca liên tiếp có thể tích lũy độ lệch chung. Không chỉ chụp hai đồng hồ bằng nhau rồi kết luận đúng giờ chuẩn.'),
 h('TC05 Dừng và chạy lại Worker'),
 p('1. Khi không có vòng đang chạy, trên A nhấn Dừng Node. Trên B quan sát Node1 mất kết nối và số online giảm. Ghi log.'),
 p('2. Trên A nhấn Khởi động với cấu hình giữ nguyên. Chờ lại 2/2 online, cùng Master Node2 và cùng phiên. Nếu khác Master/phiên, lưu bằng chứng rồi áp dụng quy trình trang 10; không bỏ qua khác biệt.'),
 p('3. Chạy đồng bộ tại Master sau khi hai máy đã thống nhất và đánh giá như TC02. Ca dừng/chạy lại Worker không thay cho ca tắt hẳn máy Master ở trang 9.'),
]))

ops.append(('8 Tắt Master và quan sát bầu chọn',[
 p('<b>TC06 - Mất máy Master thật.</b> Bắt đầu khi Node2 trên B là Master, A là Worker, cùng phiên và 2/2 online. Lưu Round ID, Master/phiên, ảnh màn hình và audit trước khi tạo sự cố.'),
 h('Thực hiện trên máy B'),
 p('1. Xác nhận không còn vòng đang chạy. Chuẩn bị người trên A quan sát và người ghi thời gian bằng đồng hồ bấm giờ độc lập trên điện thoại.'),
 p('2. Trên B chọn <b>Start → Power → Shut down</b> để tắt Windows. Không chỉ thu nhỏ ứng dụng và không dùng nút “Bầu chọn / Demo Bully” để thay cho việc tắt máy. Không cần cắt nguồn cưỡng bức.'),
 h('Quan sát trên máy A'),
 p('3. Theo dõi Node2 từ kết nối sang mất kết nối, online còn <b>1/2</b>; xem nhật ký gửi/nhận và các dòng [BULLY], LeaderChanged, ELECTION, COORDINATOR nếu có.'),
 p('4. Kết quả mong đợi: A chuyển thành <b>Master Node1</b>, có thông tin phiên mới và kết thúc bầu chọn. Ghi thời điểm B bị tắt, thời điểm A phát hiện và thời điểm A lên Master; giữ audit gốc để đối chiếu.'),
 *table(['Quan sát','Diễn giải'],[
 ['A = Master Node1; 1/2 online','Bầu chọn ở phía còn sống đã hoàn tất'],['Node2 vẫn hiển thị Master một lúc','Có thể đang chờ phát hiện lỗi; xem heartbeat và log'],['Quá 30 giây vẫn không có vai trò/phiên ổn định','Ghi nhận ca chưa đạt và thu log; 30 giây là mốc chẩn đoán của bài lab, không phải SLA'],['Nhấn Sync khi chỉ còn A','Không có Worker để đo; có thể Failed vì không có mẫu hợp lệ. Không dùng vòng solo để chứng minh hội tụ hai máy']],[.45,.55]),
 p('Cấu hình thử là heartbeat 2 giây, timeout 6 giây, election timeout 5 giây. <b>Không hứa bầu chọn chính xác sau 6 hoặc 11 giây.</b> TCP đóng sạch có thể được phát hiện sớm; mất mạng im lặng có thể phải chờ timeout và lập lịch.'),
 h('Phân biệt ba loại sự cố'),
 p('Dừng Node/đóng EXE là thử dừng tiến trình có kiểm soát; tắt Windows là thử mất máy; rút cáp/mất Wi-Fi là thử mất đường mạng. Ghi rõ loại nào đã làm. Với chỉ hai máy, mất đường mạng khi cả hai còn chạy có thể dẫn đến hai phía tự điều phối; không tuyên bố chống split-brain.'),
 p('<b>Sau bước này, chưa đánh dấu bài thử trọn vẹn đã đạt.</b> Tiếp tục kiểm tra máy B quay lại ở trang 10.'),
]))

ops.append(('9 Master cũ quay lại - bản vá R01',[
 p('<b>TC07 - Ca hồi phục phải ghi riêng.</b> Bật lại B, xác nhận IPv4 vẫn đúng (DHCP có thể đổi), rồi mở đúng EXE bằng Administrator với --data-dir C:\\BerkeleyLab\\Node2. Nhấn Khởi động nếu Node chưa chạy. Giữ A đang chạy để quan sát cơ chế tái gia nhập thực tế.'),
 h('Điều kiện cần kiểm tra'),
 p('Sau khi hiện 2/2 online, đọc <b>Master và phiên trên cả hai máy</b>. Bình thường cần thống nhất một Master và cùng phiên trước khi Sync. B là Node2 có ưu tiên ID cao hơn, nhưng không được kết luận B đã điều phối đúng chỉ từ nhãn trên máy B.'),
 p('<b>Kỳ vọng sau bản vá R01:</b> giữ A chạy, không nhấn Bầu chọn. B Node2 học phiên của A rồi bầu lại ở phiên lớn hơn; hai bên tự thống nhất Node2 và cùng phiên mới. Ví dụ Node2/1 → Node1/2 → Node2/3 hoặc cao hơn. Chờ bầu chọn kết thúc rồi Sync đủ một mẫu, hai Applied và spread đo lại. Quá 30 giây chưa thống nhất thì thu lỗi; đây là mốc chẩn đoán, không phải SLA.'),
 *table(['Nếu quan sát thấy','Team kiểm thử phải làm'],[
 ['Cùng Master và phiên','Ghi thời gian ổn định; chạy TC02 để kiểm tra đồng bộ sau hồi phục'],['Khác Master hoặc khác phiên','Không nhấn Sync; chụp cả hai cửa sổ, xuất audit của cả hai; đánh dấu TC07 chưa đạt'],['2/2 online nhưng hai bên đều Master','Ghi rõ R01; không dùng ảnh 2/2 như bằng chứng bầu chọn thành công']],[.43,.57]),
 h('Chỉ khôi phục thủ công nếu TC07 chưa đạt'),
 p('1. Lưu toàn bộ bằng chứng trạng thái lỗi trước khi thao tác. Nếu đang có vòng chạy, Dừng Node để hủy và chờ hoàn tất.'),
 p('2. Nhấn <b>Dừng Node trên cả A và B</b>. Chờ cả hai báo đã dừng. Không sửa hoặc xóa DB/cấu hình để làm mất dấu vết.'),
 p('3. Khởi động <b>B Node2 trước</b>, sau đó <b>A Node1</b>. Cả hai phải lại 2/2 online, cùng Master Node2 và cùng phiên. Nếu chưa đạt, giữ log và chuyển người phụ trách phát triển.'),
 p('4. Chạy một vòng đồng bộ sau khôi phục và ghi thành ca riêng <b>TC08</b>. Khôi phục thủ công đã được quan sát có hiệu quả trên loopback; team cần kiểm chứng lại trên hai máy thật.'),
 p('<b>TC07 đã đạt trong kiểm thử tự động sau bản vá</b>, gồm Stop/Start, tạo lại ứng dụng và timeout mặc định. Vẫn ghi kết quả LAN riêng. Không đổi TC07 thành đạt chỉ vì khôi phục thủ công TC08 đạt; hai khả năng phải có bằng chứng riêng.'),
]))

ops.append(('10 Chẩn đoán lỗi tại hiện trường',[
 *table(['Triệu chứng','Kiểm tra và hành động'],[
 ['Không có LISTEN tại 5000','Node có đang chạy TCP thật không; cổng có hợp lệ không. Kiểm tra Get-NetTCPConnection. Nếu bị chiếm, xác định PID và đóng đúng phiên Berkeley; không kết thúc tiến trình lạ.'],
 ['TcpTestSucceeded False','Node máy đích đã chạy chưa; đúng IPv4/cổng chưa; card đúng LAN không; rule đúng Private và đúng IP peer chưa; có Wi-Fi client isolation/VPN/chính sách outbound không.'],
 ['TCP True nhưng ứng dụng chưa kết nối','Kiểm tra cùng bản ZIP, Node ID không trùng, peer ID/IP/cổng đối xứng; mở đúng --data-dir. TCP True không chứng minh handshake Berkeley.'],
 ['Chỉ 1/2 online','Đọc lỗi tại cả hai máy; kiểm tra B đã bật và app đã Start. Nếu DHCP đổi IP, sửa peer và rule do nhóm tạo.'],
 ['Sync bị vô hiệu','Máy này có phải Master không; đang bầu chọn hoặc bận vòng trước không. Worker không tự kích hoạt vòng.'],
 ['PermissionDenied / Failed ở Step','Mở đúng phiên bằng Administrator; xem lỗi Windows và từng Step; dòng quyền trên UI không bảo đảm SetSystemTime thành công.'],
 ['Rejected hoặc timeout','Xem Round ID, phiên, lỗi hết hạn; kiểm tra mạng và hoạt động khác gây trễ. Không bỏ kiểm tra tuổi lệnh hay đổi ngưỡng để ép đạt.'],
 ['Giờ lại tự nhảy sau khi tạo lệch','Kiểm tra Set time automatically, W32Time và công cụ đồng bộ khác; bài đo chưa được kiểm soát.'],
 ['Hai Master hoặc khác phiên','Không Sync. Thu log của cả hai; xem R01 và quy trình khôi phục trang 10.'],
 ['Không thấy cấu hình/lịch sử đã lưu','Đang mở đúng dữ liệu Node1/Node2 hay thư mục mặc định; đang xem Demo hay TCP thật; Master nào điều phối vòng đó.']],[.33,.67]),
 h('Lệnh chỉ đọc hỗ trợ'),
 code('Get-NetTCPConnection -LocalPort 5000\nGet-NetFirewallRule -Name "BerkeleyLab-A-In5000"\n# Tren B thay ten rule thanh BerkeleyLab-B-In5000\nGet-Service W32Time\nGet-Date -Format o'),
 p('Giữ nguyên thông báo lỗi đầy đủ, thời điểm, máy, ca thử và Round ID. Nếu thay timeout để chẩn đoán, ghi cả giá trị trước/sau và chạy lại toàn bộ ca liên quan; không trộn kết quả các cấu hình trong cùng một bảng nghiệm thu.'),
]))

ops.append(('11 Khôi phục máy và lưu bằng chứng',[
 h('Xuất và sao lưu trước khi dọn'),
 p('1. Trên mỗi máy vào Nhật ký TCP / audit → <b>Xuất toàn bộ audit CSV</b>. Trên từng máy đã làm Master vào Lịch sử SQLite → <b>Xuất toàn bộ lịch sử CSV</b>. Lịch sử được lưu cục bộ, không tự gom từ hai máy.'),
 p('2. Dùng tên có ca và máy, ví dụ TC03_A_audit.csv, TC03_B_history.csv. Lưu ảnh cấu hình, Master/phiên trước-sau, bảng mẫu và Step. Không dùng nút Xóa log hiển thị thay cho xuất dữ liệu.'),
 p('3. Dừng Node trên cả hai, đóng EXE và đợi cửa sổ biến mất để flush SQLite. Sao lưu toàn bộ C:\\BerkeleyLab\\Node1 trên A và Node2 trên B, gồm cấu hình, DB và các tệp sidecar còn tồn tại. Không chép riêng DB khi app còn ghi.'),
 h('Phục hồi giờ hệ điều hành'),
 p('4. Khôi phục Set time automatically về trạng thái ban đầu đã chụp. Khôi phục trạng thái W32Time theo bản ghi: nếu ban đầu chạy/được dùng làm nguồn đồng bộ, mở lại dịch vụ và yêu cầu đồng bộ từ nguồn đã cấu hình; không tự đổi NTP/domain. [MS4, MS5]'),
 code('Start-Service -Name W32Time\nw32tm /resync\nw32tm /query /source\nGet-Date -Format o'),
 p('Khối lệnh trên dùng cho máy cần bật lại W32Time để đồng bộ. Nếu máy ban đầu cố ý để dịch vụ dừng hoặc có quy trình quản lý khác, làm theo bản ghi/quản trị viên. Nếu /resync báo không có dữ liệu thời gian, kiểm tra nguồn/mạng; chưa đánh dấu khôi phục xong chỉ vì dịch vụ đã Running.'),
 p('5. Xác nhận cả hai đã đúng nguồn giờ chuẩn và múi giờ ban đầu. Nếu không có mạng tới nguồn chuẩn, ghi chưa khôi phục hoàn tất và xử lý theo người quản lý máy. Không để máy lệch vài phút sau khi kết thúc bài thử.'),
 h('Dọn cấu hình mạng do buổi lab tạo'),
 p('Nếu không tiếp tục thử, xóa đúng rule của buổi lab trên từng máy (Administrator), không đụng rule có sẵn:'),
 code('# Tren A\nRemove-NetFirewallRule -Name "BerkeleyLab-A-In5000"\n# Tren B\nRemove-NetFirewallRule -Name "BerkeleyLab-B-In5000"'),
 p('Khôi phục DHCP/IP/profile nếu đã thay, theo bản ghi trước thử. Giữ ZIP, mã phiên bản, thư mục bằng chứng và dữ liệu đã sao lưu cho team báo cáo; không tự xóa log lỗi R01.'),
]))

ops.append(('12 Biên bản bàn giao kết quả',[
 p('Dùng biểu mẫu cho mỗi buổi thử; ghi Đạt, Chưa đạt hoặc Chưa chạy cho từng ca. Các ô trống dành để điền sau khi quan sát, không phải số đo đã xác nhận.'),
 *table(['Thông tin','Ghi tại buổi thử'],[
 ['Ngày giờ và người vận hành','........................................................................'],['Tên ZIP / SHA256 / commit','........................................................................'],['Máy A Windows / IPv4 / Node ID','........................................................................'],['Máy B Windows / IPv4 / Node ID','........................................................................'],['LAN hoặc Wi-Fi / profile / cổng','........................................................................'],['Ngưỡng spread đã chốt','........................................................................']],[.43,.57]),
 *table(['Ca','Mục tiêu','Kết quả','Bằng chứng'],[
 ['TC01','Kết nối và vai trò','........................','..................'],['TC02','Hai vòng cơ bản','........................','..................'],['TC03','Worker +2 phút','........................','..................'],['TC04','Worker -3 phút','........................','..................'],['TC05','Worker dừng và chạy lại','........................','..................'],['TC06','Tắt B và A lên Master','........................','..................'],['TC07','B quay lại tự thống nhất','........................','..................'],['TC08','Khôi phục thủ công và Sync','........................','..................']],[.12,.33,.33,.22]),
 p('Mỗi vòng ghi thêm: Round ID, Master/phiên, số online, số mẫu hợp lệ/loại, Step từng Node, spread trước/sau, ngưỡng, Outcome, thời gian bầu chọn và tên CSV/ảnh. Tách kết quả TC06/TC07/TC08. Ký xác nhận đã khôi phục giờ chuẩn và mạng: ..................................................'),
 h('Nguồn tra cứu Windows'),
 *refs(),
 p('Nguồn hành vi: bản vá R01 trên nền 0fa3a37; docs/huong-dan-windows.md; docs/sua-r01-master-tai-gia-nhap.md; TRX r01-*. Gói và hash hiện hành được ghi trong hồ sơ kỹ thuật đi kèm.','small'),
]))

report=[]
report.append(('Hồ sơ kỹ thuật\ncho team báo cáo',[
 p('<b>Dành cho người viết báo cáo và thuyết trình.</b> Hồ sơ mô tả phạm vi hệ thống, kiến trúc, thuật toán, bằng chứng hiện có, lỗi còn mở và cách trình bày số liệu từ đội kiểm thử. Các bước vận hành chi tiết nằm trong tài liệu riêng cho team kiểm thử LAN.'),
 p(f'<b>Kết luận bàn giao:</b> bản Windows đã vá R01, build thành công và có {TEST_COUNT}/{TEST_COUNT} test tự động đạt. Master cũ trở lại tự thống nhất Master/phiên và đồng bộ tiếp trên loopback. Chưa nghiệm thu nhiều máy vật lý; không tuyên bố tự hồi phục trong mọi tình huống.'),
 *table(['Thuộc tính','Bản bàn giao'],[
 ['Mốc mã nguồn','R01 + AutoSync chưa commit, trên nền 0fa3a37'],['Tích hợp main','origin/main 5cd42ae; sửa TV1 02bb90a'],['Nền tảng phát triển hiện hành','Windows 10/11 x64; .NET 10; Avalonia'],['Ứng dụng chính','src/Berkeley.Windows; BerkeleyTimeSync.exe'],['Phạm vi Mac','Đã ngưng; mã lịch sử nằm ngoài build/CI hiện hành'],['Cơ chế đồng bộ','Berkeley tự động hoặc thủ công qua TCP; Bully'],['Mức sẵn sàng','Bản thử nghiệm để chạy LAN có kiểm soát']],[.39,.61]),
 h('Cấu trúc hồ sơ'),
 *table(['Trang','Nội dung dùng cho báo cáo'],[['2','Kiến trúc và chuỗi bản tin'],['3','Thuật toán và cách đọc độ chính xác'],['4','Bầu chọn Master và lỗi tái gia nhập R01'],['5','Kết quả kiểm thử đã có và giới hạn chứng cứ'],['6','Khung thu thập và trình bày kết quả LAN'],['7','Giới hạn kết luận và nguồn tham chiếu']],[.14,.86]),
 p('Thiết kế và kiểm chứng <b>AutoSync ở trang 8</b>. Không đổi số đo Demo/clock giả thành kết quả máy thật. Các con số dưới 100 ms trong test và 24 ms ở Demo chỉ mô tả điều kiện đã chạy, không phải cam kết độ chính xác ngoài LAN.'),
]))

report.append(('1 Kiến trúc và chuỗi bản tin',[
 *table(['Thành phần','Trách nhiệm'],[
 ['Berkeley.Windows','Giao diện Avalonia: Dashboard, cấu hình, lịch sử, nguyên lý; Start/Stop/Sync/Bully/chỉnh lệch và xuất CSV'],
 ['Berkeley.Client','ViewModel và vòng đời Node; chọn demo/Windows clock thật; lưu lịch sử/audit; điều phối thao tác UI'],
 ['Berkeley.Contracts','Models, interfaces, envelope và payload dùng chung'],
 ['Berkeley.Engine','Tính mẫu/hiệu chỉnh, điều phối pha, Worker handler, WindowsClock'],
 ['Berkeley.Networking','TCP, kết nối peer, heartbeat, theo dõi sống/chết và Bully'],
 ['SQLite trên từng máy','Audit sự kiện và lịch sử vòng; không phải DB phân tán chung']],[.33,.67]),
 h('Một vòng đồng bộ bình thường'),
 p('1. Người vận hành nhấn Sync trên Master đang ổn định. Master tạo Round ID và gắn phiên lãnh đạo hiện hành.'),
 p('2. Master gửi TIME_REQUEST; Worker đọc thời điểm nhận/gửi và trả TIME_RESPONSE. Master thu t1-t4, tính RTT và offset; loại mẫu không hợp lệ hoặc quá hạn.'),
 p('3. Master tính trung bình từ offset các Worker hợp lệ và offset 0 của chính Master. Gửi ADJUST cho Worker; Worker kiểm tra Master/phiên, vòng, BootId và thời hạn cục bộ trước khi Step.'),
 p('4. Worker trả ADJUST_RESULT với Applied, PermissionDenied, Rejected hoặc Failed. Master cũng áp dụng hiệu chỉnh của mình.'),
 p('5. Master gửi yêu cầu đo lại. Chỉ có đủ mẫu Worker đã tham gia mới tính spread sau. Vòng trả về trạng thái Idle/Sẵn sàng để có thể chạy tiếp; UI hiển thị kết quả và lưu SQLite.'),
 h('Dữ liệu và vận hành'),
 p('TCP trao đổi JSON theo cùng Contracts giữa các Node. Round ID tách vòng đo; LeaderSessionId tách phiên Master; BootId phân biệt tiến trình khởi động. Dữ liệu nằm cục bộ từng máy, vì vậy phải thu audit từ cả A và B, cùng lịch sử ở các máy đã làm Master.'),
 p('Khi dùng --data-dir, demo.db và network.db ở thư mục đó. Nếu không truyền tham số, bản mới giữ thư mục lịch sử %LOCALAPPDATA%\\BerkeleyTimeSync\\CrossPlatform. WPF cũ được giữ để đối chiếu; không phải giao diện phát hành chính.'),
]))

report.append(('2 Thuật toán và độ chính xác',[
 p('Master là mốc đo tương đối trong từng vòng. Bốn thời điểm là t1 khi Master gửi, t2 khi Worker nhận, t3 khi Worker gửi lại, t4 khi Master nhận lại. Các mốc dùng UTC; thời hạn sống của thao tác dùng monotonic cục bộ.'),
 code('offset_i = ((t2 - t1) + (t3 - t4)) / 2\nRTT_i    = (t4 - t1) - (t3 - t2)\nd_avg    = (0 + sum(offset_i)) / (m + 1)\nStep_i   = d_avg - offset_i\nStep_M   = d_avg'),
 p('Trong đó m là số Worker có mẫu hợp lệ. Offset dương nghĩa Worker nhanh hơn Master theo phép ước lượng; Step âm làm giờ Worker lùi lại. Master cũng Step, không phải giữ nguyên giờ trong mọi vòng.'),
 *table(['Ví dụ lý tưởng hai máy','Worker A','Master B'],[
 ['Trước vòng, so với B','+120 giây','0 giây'],['Offset trung bình','+60 giây','+60 giây'],['Lệnh hiệu chỉnh','-60 giây','+60 giây'],['Sau vòng, theo mốc B trước vòng','+60 giây','+60 giây']],[.46,.27,.27]),
 p('Ví dụ bỏ qua độ trễ xử lý/truyền và sai số đo. Cả hai máy có thể bằng nhau nhưng cùng lệch nguồn UTC chuẩn 60 giây. Báo cáo cần phân biệt <b>hội tụ nội bộ</b> với <b>đúng giờ tuyệt đối</b>.'),
 h('Spread sau hiệu chỉnh'),
 code('spread = max(0, offset_1, ..., offset_m)\n       - min(0, offset_1, ..., offset_m)'),
 p('Offset 0 trong tập là Master tại phép đo lại. Nếu chỉ có một Worker còn lệch +500 ms mà bỏ Master, spread của một phần tử sẽ bằng 0, gây kết luận sai. Bản hiện hành có Master và không xác nhận đo lại đầy đủ nếu thiếu/sai mẫu của Worker tham gia.'),
 h('Điều kiện kết luận'),
 p('Outcome Success yêu cầu không có mẫu ban đầu bị loại, có spread đo lại và mọi Step đều Applied. Nhãn đạt ngưỡng còn kiểm tra spread ≤ ngưỡng cấu hình. Phải bổ sung số máy online/số mẫu/số Step để xác nhận đúng phạm vi toàn cụm.'),
 p('Độ trễ hai chiều bất đối xứng, jitter, lịch CPU, thao tác Step và nguồn giờ khác ảnh hưởng kết quả. Không mô tả đây là NTP đầy đủ hoặc đồng hồ tuyệt đối được chuẩn hóa. Ngưỡng mặc định 60 giây chỉ là cấu hình; đội phải chốt tiêu chí trước nghiệm thu.'),
]))

report.append(('3 Bully và lỗi tái gia nhập R01',[
 p('Bully hiện so sánh Node ID bằng thứ tự chuỗi không phân biệt hoa thường. Dùng Node1/Node2 cho bài hai máy; Node2 có ưu tiên cao hơn Node1. Không suy ra thứ tự số đúng cho mọi tên tùy ý như Node10/Node2.'),
 p('Khi Master mất kết nối/heartbeat, Worker khởi xướng ELECTION tới các Node ID ưu tiên cao hơn. Node cao hơn trả OK và có thể bầu tiếp; ứng viên thắng gửi COORDINATOR. Phiên mới làm vòng cũ bị hủy. Khi hai máy mất B, A có thể lên Master nhưng chỉ còn 1/2 online, không có Worker để chứng minh hội tụ.'),
 h('R01 trước và sau bản vá'),
 *table(['Giai đoạn','A Node1','B Node2'],[
 ['Ban đầu','Worker; Master Node2; phiên 1','Master Node2; phiên 1'],['B dừng, A bầu xong','Master Node1; phiên 2','Đã dừng'],['Trước vá: B quay lại; chờ 30 giây','Master Node1; phiên 2','Master Node2; phiên 1'],['Sau vá: B quay lại, tự bầu','Worker; Node2/phiên ≥3','Master Node2; cùng phiên ≥3']],[.42,.29,.29]),
 p('TCP loopback với clock giả: ba biến thể R01 thất bại trên mã cũ và đạt sau vá; gồm timeout mặc định 2000/6000/5000 ms. Stop/Start và tạo lại ClientSession đều được kiểm tra. Hai ca nhanh lặp ba chu kỳ, ca mặc định một chu kỳ; mỗi lần Sync đủ hai Applied.'),
 h('Giải thích theo mã nguồn'),
 p('Nguyên nhân cũ: phiên đặt lại bằng 1 và heartbeat chỉ báo online. Bản vá cho Master phát lại COORDINATOR định kỳ. Node quay lại học phiên survivor; node ưu tiên cao hơn bầu ở phiên lớn hơn, node thấp hơn nhận Master/phiên hiện tại. ELECTION cũng cập nhật phiên lớn nhất trước khi trả OK và tự bầu.'),
 h('Ảnh hưởng và cách báo cáo'),
 p('Từ chối phiên cũ; không phát tuyên bố đã bị phiên mới vượt; bản phát lại trùng không tạo LeaderChanged hay phiên mới. Stop đợi bầu chọn hủy xong. Ba Client cũng tự hội tụ khi Node3 quay lại và Sync đủ ba Applied. Cần chạy TC07 trên LAN thật; nếu bất đồng thì thu log, không Sync.'),
 p('Không tuyên bố chịu được chia cắt mạng theo quorum hoặc tránh mọi split-brain. Ca mất Master, ca Master cũ quay lại và ca khôi phục thủ công phải có kết quả riêng. Nút Bầu chọn trên UI không tương đương làm máy khác crash.'),
]))

report.append(('4 Bằng chứng kiểm thử hiện có',[
 p(f'Bộ test Release R01 + AutoSync: <b>{TEST_COUNT}/{TEST_COUNT} đạt, 0 lỗi test, 0 bỏ qua</b>; build 0 warning, 0 error. Bộ tại merge 0fa3a37 có 98 test. Bổ sung hồi quy R01 và AutoSync, mở rộng ca ba Client. Đây là TCP loopback, không phải LAN vật lý.'),
 *table(['Bộ test','Đạt','Phạm vi nổi bật'],[
 ['Engine','54','UTC lệch nhưng lệnh còn hạn; hết hạn monotonic; sai vòng/phiên/BootId; spread có Master; thiếu mẫu; hủy/lỗi và retry'],
 ['Networking','15','TCP, heartbeat, Bully; học phiên, chặn bản cũ, mất bản tin đầu/phát lại, dừng khi đang bầu'],
 ['Desktop WPF cũ','18','Đối chiếu chức năng cũ, lưu trữ, cấu hình và vận hành'],
 ['Client Windows mới',str(RELEASE['tests']['WindowsClient']),'ViewModel, Step, lịch sử/CSV; R01; AutoSync định kỳ/tái gia nhập, chặn chồng vòng, cấu hình; 2/3/6 Client']],[.28,.10,.62]),
 p('Ca sáu Client dùng TCP thật trên cùng máy và clock giả: hai vòng liên tiếp, đủ 6/6 kết nối, cùng Node6/phiên, 5 mẫu hợp lệ, 0 mẫu loại, 6 Applied, spread và độ chênh sáu clock dưới 100 ms trong điều kiện test. Không suy ra độ chính xác tương tự trên sáu laptop.'),
 h('Hợp nhất sửa lỗi TV1'),
 p('Sửa 02bb90a bỏ phép trừ UTC giữa hai máy khi xét tuổi ADJUST và thêm Master vào spread. Nhánh UI đã có cách sửa tương ứng; merge giữ thêm lease monotonic, kiểm tra đủ mẫu, hủy vòng và audit Step cho UI. Test “UTC cũ nhưng còn hạn” được tách khỏi test “hết hạn thực sự”, không đổi lệnh hết hạn sang Applied.'),
 h('Giao diện và đóng gói đã kiểm tra'),
 p('Đã thao tác Demo qua UI sau merge: Success, 5 mẫu Worker/6 Step, spread 24.0 ms trong lần đó, vòng lưu vào SQLite. Gói Windows x64 self-contained tạo thành công; hash DLL Engine/Networking/Client khớp các DLL vừa kiểm thử. Chẩn đoán Windows chỉ đọc giờ/token, không chỉnh giờ hệ điều hành máy phát triển.'),
 h('Chứng cứ hồi quy R01'),
 p('<b>Trước vá: 0/3; sau vá: 3/3 biến thể R01 đạt.</b> Mỗi chu kỳ tự thống nhất Master/phiên rồi Sync thành công; Worker trở lại học phiên hiện tại. Ca ba node giữ các assert hội tụ/Applied; deadline chứa thêm bầu chọn và TCP tới node offline được tăng từ 5 lên 10 giây.'),
 p('Chứng cứ: tests/*/TestResults/r01-*.trx; docs/sua-r01-master-tai-gia-nhap.md; artifacts/release/latest-windows.json. Quan sát cũ giữ tại artifacts/handoff/rejoin-observation.json. Các thư mục artifacts/TestResults bị Git ignore; chép cùng hồ sơ khi lưu bằng chứng.','small'),
]))

report.append(('5 Khung báo cáo kết quả LAN thật',[
 p('Đội báo cáo chỉ điền số đo sau khi nhận biên bản, CSV và ảnh của đội kiểm thử. Ghi phần chưa chạy là “Chưa thực hiện”; không điền giá trị mong đợi vào cột thực đo.'),
 *table(['Mục cần công bố','Dữ liệu thực tế phải thu'],[
 ['Môi trường','Hai model máy, CPU/RAM, Windows build, Ethernet/Wi-Fi, router/switch, IPv4/subnet/profile, phiên bản ZIP/commit'],
 ['Cấu hình','Node ID/cổng/peer hai phía; heartbeat/election/sample/round timeout; tuổi mẫu; ngưỡng được chốt'],
 ['Kiểm soát giờ','Nguồn chuẩn; W32Time/Set time automatically trước-trong-sau; quyền và múi giờ'],
 ['Mỗi vòng','Round ID, Master/phiên, số online, mẫu hợp lệ/loại, Step từng Node, spread trước/sau, Outcome, tên CSV/ảnh'],
 ['Sự cố','Cách tắt máy/mất mạng/dừng Node; thời gian phát hiện và bầu lại đo bằng đồng hồ độc lập; Master/phiên ở cả hai phía'],
 ['Hồi phục','Kết quả tái gia nhập tự động riêng; workaround riêng; Sync sau khi đã thống nhất Master/phiên']],[.29,.71]),
 h('Bảng số liệu tối thiểu cho báo cáo'),
 *table(['Ca','Lệch ban đầu','Spread sau','Step','Kết luận thực đo'],[
 ['Cơ bản','..............','..............','..............','........................'],['A +2 phút','..............','..............','..............','........................'],['A -3 phút','..............','..............','..............','........................'],['Sau Worker restart','..............','..............','..............','........................']],[.25,.18,.18,.15,.24]),
 p('Với bài hai máy, yêu cầu trước vòng là 2/2 online, 1 mẫu Worker hợp lệ, 2 Applied và spread đo lại có giá trị. Mốc ≤1.0 giây là đề xuất vận hành, chỉ dùng khi người phụ trách đã chốt. Với failover, trình bày riêng TC06 mất Master, TC07 tái gia nhập và TC08 khôi phục thủ công.'),
 h('Gợi ý cấu trúc trình bày'),
 p('Bối cảnh và phạm vi → kiến trúc → công thức Berkeley → Bully và phiên → kịch bản/môi trường thực nghiệm → bảng kết quả kèm bằng chứng → lỗi R01, hạn chế và hướng phát triển. Khi có nhiều lần đo, công bố số lần, điều kiện và cách tổng hợp; không chọn riêng lần tốt nhất.'),
]))

report.append(('6 Giới hạn và nguồn tham chiếu',[
 h('Những điều chưa được chứng minh'),
 bullet('Chưa nghiệm thu chính xác giờ hệ điều hành trên 2/3/6 máy Windows vật lý; chưa có số đo LAN thật trong hồ sơ này.'),
 bullet('R01 đã vá và đạt trên loopback; chưa nghiệm thu tắt/bật hai máy vật lý. Khi liên lạc phục hồi, vẫn chờ cùng Master/phiên trước Sync; online riêng lẻ chưa đủ.'),
 bullet('AutoSync đã có cho TCP, cấu hình mới mặc định bật, chu kỳ 30 giây; cấu hình và ca riêng ở trang 8. Nút thủ công vẫn dùng được. App không tự khởi động cùng Windows chỉ vì bật tùy chọn khởi động Node khi mở app.'),
 bullet('Peer chưa được xác thực/mã hóa. Chỉ thử trên LAN tin cậy, không mở cổng ra Internet; chưa có bảo đảm quorum/chống split-brain hay xử lý mọi phân vùng mạng.'),
 bullet('Gói thử chưa có quy trình cài đặt/ký mã hoàn chỉnh; cần thử thêm DPI/màn hình, sleep/wake và tải dài hạn. Step làm đổi cả giờ Master; phải khôi phục nguồn giờ chuẩn.'),
 h('Mốc phát hành và truy vết'),
 code(f'Nguồn: bản vá R01 chưa commit; nền 0fa3a37\nZIP: {ZIP_NAME}\nSHA256:\n{ZIP_HASH}'),
 p('ZIP trên chứa bản vá R01; DLL Engine/Networking/Client được đối chiếu với DLL chạy test. Manifest lưu hash và số test. Phiên nằm trong RAM; toàn cụm khởi động lại có thể về phiên 1. Bản vá không tạo epoch bền vững hoặc bảo đảm quorum.'),
 h('Mã nguồn và chứng cứ nội bộ'),
 p('src/Berkeley.Engine(tv1)/Runtime/WorkerSyncHandler.cs; SyncRoundOrchestrator.cs; src/Berkeley.Networking(tv2)/Election/BullyLeadershipService.cs; tests/Berkeley.Client.Tests/WindowsClientTests.cs; tests/Berkeley.Networking.Tests/BullyLeadershipServiceTests.cs; docs/sua-r01-master-tai-gia-nhap.md.','small'),
 h('Nguồn Windows đã đối chiếu ngày 21 tháng 11 năm 2026'),
 *refs(),
 p('Tài liệu vận hành đi kèm là đầu vào cho đội kiểm thử. Kết quả có số đo, thời gian và người xác nhận từ buổi thử thật mới là đầu vào cho phần nghiệm thu trong báo cáo.','small'),
]))

ops.append(('13 Tự động đồng bộ - AutoSync',[
 p('Chạy ca này sau TC01-08. AutoSync chỉ áp dụng TCP thật, có thể chỉnh giờ cả Master và Worker. Dùng cùng gói mới trên hai máy và bảo đảm quyền chỉnh giờ Windows.'),
 h('Bật và cấu hình'),
 p('Trên cả A và B, vào Cấu hình mạng, bật <b>AutoSync · định kỳ và khi node trở lại (TCP)</b>, nhập chu kỳ <b>30 giây</b>, chọn Lưu và khởi động lại. Cấu hình mới mặc định AutoSync bật; chu kỳ hợp lệ 1-86400 giây. Cấu hình được giữ khi mở lại ứng dụng. Demo dùng nút thủ công.'),
 h('TC09 Vòng đầu và vòng định kỳ'),
 p('Không bấm Đồng bộ ngay. Chờ 2/2 online, cùng Master/phiên, bầu chọn kết thúc; Master chờ các peer xác nhận và ổn định rồi tự chạy vòng Berkeley. Quan sát Measuring → Adjusting → Verifying → Sẵn sàng. Kiểm tra lịch sử có vòng mới, một mẫu Worker, hai Applied và spread đo lại.'),
 p('Đợi thêm ít nhất hai vòng định kỳ. Chu kỳ tính từ khi vòng tự động trước kết thúc bằng monotonic; TCP I/O và lịch CPU có thể kéo dài thời gian. Không lấy giờ Windows vừa Step làm đồng hồ bấm thời gian.'),
 h('TC10 Worker trở lại'),
 p('Dừng A; đợi B thấy mất kết nối. Chạy lại A. Không bấm Sync. Khi cả hai cùng Master/phiên và ổn định, phải tự có vòng đủ hai Applied. Ghi Round ID và audit hai phía.'),
 h('TC11 Master cũ trở lại'),
 p('Dừng B, chờ A lên Master phiên mới. Chạy lại B và giữ A hoạt động. Chờ hai máy tự thống nhất Node2 ở phiên cao hơn, rồi tự có vòng đồng bộ. Không dùng nút Bầu chọn hoặc Sync để thay cho kiểm tra tự động.'),
 h('TC12 Thủ công, dừng lịch và bằng chứng'),
 p('Giữa các vòng, bấm Đồng bộ ngay trên Master và kiểm tra vẫn chạy được. Trong vòng đang chạy, các thao tác xung đột bị chặn; Dừng Node vẫn hủy được. Dừng cả hai và chờ hơn một chu kỳ: không có vòng mới. Tắt AutoSync để quay lại lab thủ công, xuất lịch sử/audit và khôi phục nguồn giờ chuẩn.'),
 p('Nếu peer chỉ online nhưng chưa xác nhận Master/phiên hoặc dùng bản cũ, AutoSync chờ. Thiếu quyền hoặc lỗi Step được ghi vào kết quả, không coi là đã hội tụ. Ghi TC09-12 riêng trong biên bản; các số liệu LAN vẫn để trống đến khi thực đo.'),
]))
report.append(('7 Thiết kế và kiểm chứng AutoSync',[
 p('AutoSync tái sử dụng một vòng Berkeley đầy đủ: đo t1-t4, tính Step cho Worker và Master, rồi đo lại. Không Step thêm lần nữa theo mẫu cũ sau khi vòng đã kết thúc.'),
 h('Điều kiện tự điều phối'),
 p('Mọi Client TCP phát SYNC_STATUS chứa Master/phiên và trạng thái bầu chọn, kể cả khi AutoSync tắt. Master chỉ tự chạy khi có ít nhất một peer online; tất cả peer online báo cùng Master/phiên, không bầu chọn và báo cáo còn hạn theo monotonic.'),
 p('Trạng thái phải ổn định max(250 ms, hai nhịp báo cáo). Nhịp báo cáo là heartbeat interval giới hạn 100-1000 ms, còn phụ thuộc TCP I/O. BootId, phiên và tập peer online xác định một trạng thái cụm; thay đổi sẽ yêu cầu xác nhận ổn định lại.'),
 h('Lịch, vòng đời và nút thủ công'),
 p('Sau vòng đầu/tái gia nhập, Master chạy định kỳ theo monotonic từ cuối vòng trước. Chung khóa thao tác với nút thủ công, không chồng vòng, không chạy bù dồn. Kiểm tra lại Master/phiên trước chạy; Engine hủy vòng khi đổi lãnh đạo. Stop/đóng ứng dụng hủy lịch và đợi kết thúc.'),
 p('Cấu hình mới mặc định AutoSync bật; chu kỳ 30 giây, hợp lệ 1-86400 giây. Bật trên mọi máy muốn tự điều phối khi lên Master. Chỉ hỗ trợ TCP, Demo dùng nút thủ công. Cần cùng gói mới để peer xác nhận SYNC_STATUS; thiếu xác nhận chỉ chặn tự động.'),
 h('Chứng cứ và giới hạn'),
 p('Test TCP loopback/clock giả kiểm tra vòng định kỳ, Worker/Master quay lại tự đồng bộ, chỉ online chưa đủ, nút thủ công, không chồng vòng, Stop không sinh vòng mới, lịch sử và lưu/mở lại cấu hình, từ chối chu kỳ sai. Giữ bộ R01 và 2/3/6 node.'),
 p('Chứng cứ: Engine/Networking/Desktop: autosync-full.trx; Client: autosync-client-final.trx; đường dẫn đầy đủ trong latest-windows.json. Thiết kế: docs/tu-dong-dong-bo.md; AutomaticSyncCoordinator.cs; WindowsClientTests.cs. AutoSync chạy trên các peer đang tham gia, không bảo đảm mọi peer cấu hình đều online. Chưa có quorum hoặc nghiệm thu Step đa máy vật lý.'),
]))
results=[
 build('01_Huong_dan_van_hanh_kiem_thu_LAN_Windows_v1_2.pdf','TEAM KIỂM THỬ  |  HƯỚNG DẪN VẬN HÀNH',ops,'ops'),
 build('02_Ho_so_ky_thuat_team_bao_cao_v1_2.pdf','TEAM BÁO CÁO  |  HỒ SƠ KỸ THUẬT',report,'report'),
]
(ROOT/'artifacts/handoff').mkdir(parents=True, exist_ok=True)
(ROOT/'artifacts/handoff/layout_manifest.json').write_text(json.dumps(results,indent=2,ensure_ascii=False),encoding='utf-8')
