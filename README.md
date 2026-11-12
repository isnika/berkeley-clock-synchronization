# BerkeleyTimeSync

Ứng dụng Windows đồng bộ đồng hồ mạng nội bộ bằng thuật toán Berkeley.  
Môn: Các Hệ Thống Phân Tán | Nhóm 6 thành viên | .NET 10 LTS + WPF + TCP + SQLite

---

## Cấu trúc dự án

```
berkeley-clock-synchronization/
├── src/
│   ├── Berkeley.Contracts(chung)/  # Interfaces & data models chung (TV1 quản lý)
│   ├── Berkeley.Engine(tv1)/       # Thuật toán Berkeley + Windows Clock (TV1)
│   ├── Berkeley.Networking(tv2)/   # TCP transport + Bully election (TV2)
│   └── Berkeley.Desktop(tv3)/      # WPF GUI + SQLite + packaging (TV3)
├── tests/
│   ├── Berkeley.Engine.Tests/
│   ├── Berkeley.Networking.Tests/
│   ├── Berkeley.Desktop.Tests/
│   └── Berkeley.Integration.Tests/
├── docs/
│   └── phan-tich-do-an-berkeley.md
└── BerkeleyTimeSync.sln
```

## Yêu cầu

- .NET 10 SDK: https://aka.ms/dotnet/download
- Windows 10/11 (WPF + SetSystemTime)
- Chạy với quyền Administrator để chỉnh giờ hệ thống

## Build & Test

```bash
dotnet build BerkeleyTimeSync.sln
dotnet test tests/Berkeley.Engine.Tests/
```

## Phân công

| Module | Thành viên | Trách nhiệm |
|--------|-----------|-------------|
| Berkeley.Contracts + Berkeley.Engine | TV1 | Thuật toán Berkeley, Windows Clock, điều phối vòng |
| Berkeley.Networking | TV2 | TCP, Bully, heartbeat, membership |
| Berkeley.Desktop | TV3 | WPF GUI, SQLite, cấu hình, đóng gói |
