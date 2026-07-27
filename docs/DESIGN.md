# Barbatos.Migration — Tài liệu thiết kế

**Phiên bản kiến trúc:** 3.1
**Thay thế:** bản phác thảo 2.1.0-PROD
**Nền tảng:** .NET 8 / 9 / 10 (WPF, MAUI, console; Unity từ khi Unity chuyển sang CoreCLR)

---

## Mục lục

1. [Review bản 2.1 — những chỗ chưa ổn](#1-review-bản-21--những-chỗ-chưa-ổn)
2. [Nguyên tắc thiết kế](#2-nguyên-tắc-thiết-kế)
3. [Mô hình version: data version ≠ app version](#3-mô-hình-version-data-version--app-version)
4. [Hai installation model](#4-hai-installation-model)
5. [Vòng đời một lần chạy](#5-vòng-đời-một-lần-chạy)
6. [Crash-safety: journal + atomic swap](#6-crash-safety-journal--atomic-swap)
7. [Core contracts](#7-core-contracts)
8. [Progress & cancellation](#8-progress--cancellation)
9. [Update trigger mode: Silent vs Manual](#9-update-trigger-mode-silent-vs-manual)
10. [Tích hợp Barbatos.Wpf.Core](#10-tích-hợp-barbatoswpfcore)
11. [Cấu trúc solution & package](#11-cấu-trúc-solution--package)
12. [Kiểm chứng bằng test](#12-kiểm-chứng-bằng-test)
13. [Lộ trình MAUI & Unity](#13-lộ-trình-maui--unity)

---

## 1. Review bản 2.1 — những chỗ chưa ổn

Bản phác thảo đúng về **định hướng**: tách Core zero-dependency, multi-provider trong một step,
hai installation model, progress + cancellation + rollback, chain aggregation cho skip-version.
Những ý đó đều được giữ nguyên.

Nhưng phần **triển khai** trong tài liệu có một số lỗ hổng đủ nghiêm trọng để mất dữ liệu người
dùng trong sản xuất. Dưới đây là toàn bộ danh sách, xếp theo mức độ.

### 1.1. Nhóm nghiêm trọng — có thể mất dữ liệu

| # | Vấn đề trong bản 2.1 | Hậu quả thực tế | Cách sửa ở 3.0 |
|---|---|---|---|
| 1 | **`RollbackAsync` xoá thư mục dữ liệu trước rồi mới copy backup vào.** | Giữa `Directory.Delete(WorkingDataDirectory)` và lúc copy xong, người dùng **không có bản dữ liệu nào cả**. Với DB vài GB thì cửa sổ này dài hàng phút. Mất điện trong khoảng đó = mất sạch. | `DirectoryOperations.Replace()`: rename thư mục hiện tại sang `discard-*`, rename snapshot vào chỗ cũ, xong mới xoá `discard`. Rename cùng volume là atomic → **mọi thời điểm đều tồn tại một bản đầy đủ**. |
| 2 | **Không có cơ chế chống crash.** Rollback chỉ chạy trong `catch`. | Kill process từ Task Manager, mất điện, Windows Update restart → dữ liệu nằm giữa chừng, lần khởi động sau không ai biết. Đây là chuyện *bình thường* trong vài phút migration chạy. | `IMigrationJournal` ghi trước khi byte đầu tiên thay đổi, xoá sau khi commit. Lần chạy sau thấy journal → `RecoverAsync()` khôi phục snapshot rồi mới lên kế hoạch mới. |
| 3 | **Không ai ghi lại data version sau khi migration thành công.** `IMigrationContext.CurrentDataVersion` chỉ có getter. | Lần khởi động sau engine lại chạy **toàn bộ** step từ đầu. Framework về cơ bản không hoạt động qua nhiều lần chạy. | `IDataVersionStore` (đọc **và** ghi). Engine đóng dấu version + ledger step id ở bước commit cuối cùng. |
| 4 | **`BackupDirectory` không được kiểm tra vị trí.** | Nếu backup nằm trong data directory (rất dễ xảy ra: `AppData/Data/backup`) thì snapshot **copy chính nó** → phình vô hạn; và restore sẽ xoá luôn snapshot. | `PathGuard.EnsureDisjoint()` + `EnsureSafeToDelete()` chặn ngay lúc `Validate()`, kèm cả chặn drive root / thư mục hệ thống. |
| 5 | **Rollback thất bại bị nuốt.** `RollbackAsync` không được bọc, và `catch (Exception)` ở ngoài sẽ bắt luôn lỗi từ rollback. | Trạng thái tệ nhất — dữ liệu hỏng mà app vẫn báo "đã revert". | Rollback chạy **ngoài** khối `catch` gốc, có `try` riêng. Thất bại → `MigrationOutcome.RollbackFailed`, log `Critical`, **giữ nguyên** journal + snapshot để lần sau thử lại, `CanContinue = false`. |
| 6 | **SQLite provider không đóng file handle.** `ClearAllPools()` chỉ gọi ở đầu, không gọi ở cuối; connection vẫn trong pool. | Trên Windows, snapshot/restore/rename thư mục chứa file `.db` đang mở → `IOException: file in use`. **Rollback không chạy được vì cái nó đang rollback vẫn đang mở.** | `Pooling=false` trên connection string + `Close()`/`Dispose()`/`ClearAllPools()` trong `finally` trên mọi đường thoát. |
| 7 | **Không checkpoint WAL.** | DB ở chế độ WAL giữ commit gần nhất trong file `-wal`. Snapshot chỉ copy `.db` → mất các commit gần nhất một cách âm thầm. | `PRAGMA wal_checkpoint(TRUNCATE)` trước khi nhả connection. |
| 8 | **Không có khoá liên tiến trình.** | Hai instance app (double-click shortcut) hoặc app + updater cùng migrate một thư mục. | `IMigrationLock` — file mở `FileShare.None` + `DeleteOnClose`, OS tự nhả khi process chết nên không cần timeout đoán mò. |

### 1.2. Nhóm chức năng — thiếu hoặc sai logic

| # | Vấn đề | Cách sửa ở 3.0 |
|---|---|---|
| 9 | **Side-by-side strategy hoàn toàn không được viết**, dù chiếm một nửa thiết kế. Và `IMigrationContext` chỉ có một `WorkingDataDirectory` bất biến nên *không thể* biểu diễn nó (provider phải ghi vào thư mục v2.0 mới, đọc từ v1.0 cũ). | `SideBySideStrategy` đầy đủ. `IMigrationContext` tách thành `OriginalDirectory` (chỉ đọc) / `WorkingDirectory` (nơi provider ghi) / `BackupDirectory`. Strategy là thứ dịch chuyển `WorkingDirectory`. |
| 10 | **`DownAsync` có trong interface nhưng engine không bao giờ gọi.** Tài liệu quảng cáo "Bi-directional" nhưng không có đường downgrade nào. | `MigrationPlan` phát hiện `current > target`, sắp xếp step giảm dần, gọi `DownAsync`. `IMigrationProvider.CanDown` cho phép **kiểm tra trước khi động vào dữ liệu** — plan không khả thi thì fail ngay, không phải chết giữa chừng. |
| 11 | **Không xử lý trường hợp data mới hơn app.** Engine thấy `pendingSteps` rỗng → báo thành công → app crash khi đọc schema lạ. | `MigrationOutcome.Blocked` với thông báo rõ ràng; `AllowDowngrade` mở đường downgrade khi mọi step đều đảo được. |
| 12 | **`ProcessAppStartupUpdateAsync` trả `false` cho cả "user hoãn" lẫn "migration lỗi".** Caller không phân biệt được "chạy tiếp bằng dữ liệu cũ" với "đừng chạy". | `MigrationResult` với `Outcome` 7 trạng thái + `CanContinue` riêng. `IsSuccess` và `CanContinue` là **hai câu hỏi khác nhau**: rollback sạch thì dữ liệu nguyên vẹn nhưng vẫn ở version cũ. |
| 13 | **Lẫn lộn app-update với data-migration.** Tài liệu bàn "Remind me later", "Skip this version" — hợp lý cho việc *tải bản mới*, nhưng vô lý cho việc *chuyển đổi dữ liệu*: binary mới đã đang chạy rồi, hoãn nghĩa là chạy schema mới trên dữ liệu cũ. | Framework này chỉ làm **data migration**. `UpdateTriggerMode` chỉ quyết định *có hỏi trước không*. `AllowRunningOnOlderData` là thứ quyết định "hoãn" có thật sự là lựa chọn hay không, và `MigrationPromptContext.CanDefer` truyền điều đó xuống UI để không hiện nút dẫn đến ngõ cụt. |
| 14 | Engine nhận `IEnumerable<IMigrationStep>` **không validate**: trùng `TargetVersion`, trùng id. | `MigrationPlan.Create()` validate và ném `MigrationPlanException` với thông báo cụ thể. Engine gọi nó **ngay trong constructor** → sai cấu hình lộ ra lúc khởi động, không phải trên máy người dùng đang ở sau 3 version. |
| 15 | Không có `IMigrationStep.Id`. | Có `Id` ổn định để ghi ledger — biết chính xác bản cài này đã áp step nào, kể cả khi version được đánh lại. |
| 16 | Không kiểm tra dung lượng đĩa trống dù tài liệu tự nhắc tới DB vài GB. | `RequiredFreeSpaceFactor` (mặc định 1.2) kiểm tra **trước** khi copy. Hết đĩa giữa chừng chính là lúc rollback dễ hỏng nhất. |
| 17 | Backup xoá ngay khi thành công. | `BackupRetentionCount` (mặc định 1) — người dùng phát hiện dữ liệu sai vào hôm sau vẫn còn đường lùi. |
| 18 | Không có logging. | `IMigrationLogger` (1 method, zero-dependency; package DI có adapter sang `ILogger`). Log là bằng chứng duy nhất khi có report mất dữ liệu. |

### 1.3. Nhóm kỹ thuật / chất lượng

| # | Vấn đề | Cách sửa ở 3.0 |
|---|---|---|
| 19 | **`new Progress<T>()` trong vòng lặp của engine.** `Progress<T>` post qua `SynchronizationContext` → report đến **bất đồng bộ, có thể sai thứ tự, và có thể đến sau khi migration đã xong**. | `ProgressRelay` là `IProgress<T>` thuần, relay **đồng bộ**. Việc marshal về UI thread là của caller (một lần, ở biên) — trên WPF là `MigrationRunner` + `IDispatcher`. |
| 20 | Progress có thể **lùi**: provider thứ hai của một step bắt đầu report lại từ 0. | `ProgressRelay` giữ high-water mark → phần trăm đơn điệu tăng. Progress bar lùi lại thì người dùng đọc là app hỏng. |
| 21 | Chia đều 100% cho mỗi step/provider. | `IMigrationProvider.Weight` — provider viết lại 1 triệu dòng khai báo weight lớn hơn provider đổi tên một key. SQLite provider mặc định weight 4.0. |
| 22 | `MigrationProgress` là class mutable, cấp phát mới mỗi lần report. | `readonly struct`. Provider có thể report hàng nghìn lần; không nên tạo áp lực GC lên tác vụ vốn đã nặng I/O. |
| 23 | `MigrationProgress.TargetVersion = null!` nhưng engine tạo report không set → NRE cho consumer. | `Version?` đúng nghĩa. |
| 24 | Handler progress ném exception sẽ giết migration. | `ProgressRelay.Emit` nuốt exception từ handler — lỗi data-binding trên UI thread không được phép kích hoạt rollback. |
| 25 | **Chỉ có một package cho SQLite.** Migration là bài toán chung của mọi CSDL quan hệ, và EF Core cho thấy phạm vi đó rộng đến đâu. | Tách thành hai: `Barbatos.Migration.Database` (ADO.NET thuần, **không phụ thuộc driver nào**, chạy với SQLite/SQL Server/PostgreSQL/MySQL/Oracle) và `Barbatos.Migration.EntityFrameworkCore` (chạy migration của chính EF Core bên trong snapshot). Phần đặc thù từng engine gom vào `IDatabaseDialect` — xem §11.1. |
| 26 | Core "zero-dependency" nhưng dùng `System.Text.Json` để lưu trạng thái. | Journal + version stamp dùng format `key=value` tự viết (`KeyValueFile`): không dependency, đọc được bằng Notepad khi support khách hàng. Ghi qua temp file + `File.Replace` + `Flush(flushToDisk: true)`. |
| 27 | `IInstallationStrategyHandler.CommitAsync`/`RollbackAsync` không report progress. | Có `IProgress` — restore 5 GB mà UI đứng im không phản hồi là không chấp nhận được. Đổi lại chúng **không** nhận `CancellationToken`: commit/rollback không được phép huỷ. |
| 28 | Không có cách xem trước migration sẽ làm gì. | `MigrationEngine.CreatePlan()` thuần tính toán, không I/O ghi — dùng cho dialog xác nhận, màn hình chẩn đoán, và test. |
| 29 | ViewModel mẫu chạy engine ngay trên UI thread. | `MigrationRunner` chạy engine trên thread pool. Copy thư mục là I/O **đồng bộ**; chạy trên UI thread thì splash screen đứng hình đúng lúc người dùng cần thấy nó động nhất. |
| 30 | Không throttle progress về UI. | `MigrationRunner` throttle 50 ms / 0.5%, nhưng luôn cho qua report ≥ 99.9% và các phase quan trọng. |

---

## 2. Nguyên tắc thiết kế

1. **Không bao giờ có thời điểm nào mà không tồn tại một bản dữ liệu đầy đủ.** Mọi thao tác thay
   thế thư mục là chuỗi rename, không phải delete-rồi-copy.
2. **Mọi thứ có thể chết giữa chừng.** Journal + recovery là bắt buộc, không phải tính năng thêm.
3. **Engine không đụng file system.** Engine điều phối; `IInstallationStrategy` biết dữ liệu ở
   đâu và cách bảo vệ; `IMigrationProvider` biết cách biến đổi. Thêm model thứ ba = viết một
   class, không sửa engine.
4. **Sai cấu hình phải lộ lúc khởi động**, không phải trên máy khách.
5. **Core không có dependency nào.** Chạy được ở nơi không có DI container, không có
   `System.Text.Json`, không có JIT.
6. **Thông báo lỗi viết cho người dùng cuối**, không phải cho developer.

---

## 3. Mô hình version: data version ≠ app version

Đây là điểm bản 2.1 chưa tách bạch, và nó quyết định toàn bộ phần còn lại.

- **App version** — `AppInfo.Version`, đổi mỗi lần build.
- **Data version** — hình dạng dữ liệu trên đĩa. **Chỉ đổi khi có step làm nó đổi.**

App ship 1.4.1 → 1.4.2 → 1.4.3 mà không có step nào thì data version vẫn đứng yên ở 1.4.0.
Đó là lý do data version phải được **lưu riêng** chứ không suy ra từ app version.

```
IDataVersionStore
 ├── Read()               → Version?  (null = chưa từng đóng dấu)
 ├── ReadAppliedStepIds() → ledger toàn bộ step đã áp
 └── Write(version, stepIds)
```

Mặc định `FileDataVersionStore` ghi file `.migration-version` **bên trong** thư mục dữ liệu nó
mô tả. Cố ý như vậy: thư mục được copy / clone / restore sẽ **mang theo version của chính nó**.
Đó là thứ khiến side-by-side clone một thư mục và bản clone tự báo đúng version xuất phát, và
khiến restore snapshot cũng khôi phục luôn version.

**Dữ liệu chưa có dấu** (`Read()` trả `null`) rơi vào đúng hai trường hợp, và phân biệt chúng
rất quan trọng:

- **Cài mới hoàn toàn** — không có gì để migrate.
- **Bản cài cũ có từ trước khi có framework này** — có dữ liệu thật ở hình dạng cũ.

`MigrationOptions.InitialDataVersion` quyết định. Mặc định `0.0.0.0` (chạy lại mọi step, mô
hình "migration tự dựng schema" như EF Core). Package WPF suy ra thông minh hơn — xem §10.

---

## 4. Hai installation model

```
                        ┌──────────────────────────┐
                        │     MigrationEngine      │
                        └────────────┬─────────────┘
                                     │  IInstallationStrategy
                   ┌─────────────────┴─────────────────┐
                   ▼                                   ▼
        InPlaceSingleFolder                  SideBySideMultiFolder
        AppData/App/Data/                    AppData/App/1.0.0/
                                             AppData/App/2.0.0/
        Prepare : snapshot toàn bộ           Prepare : clone → staging-<id>
        Migrate : ghi đè trực tiếp           Migrate : ghi vào staging
        Commit  : đóng dấu + giữ backup      Commit  : rename staging → 2.0.0/
        Rollback: swap snapshot về (3 rename)Rollback: xoá staging (bản cũ chưa từng bị mở ghi)
        Downgrade: được, nếu mọi step đảo    Downgrade: chạy lại binary cũ
        Chi phí : 2× dung lượng khi chạy     Chi phí : 2× khi chạy + 1× mỗi version giữ lại
```

| Tiêu chí | In-Place | Side-By-Side |
|---|---|---|
| **Hợp với** | Mobile (MAUI), WPF nhẹ, SaaS client, tiện ích | Phần mềm chuyên nghiệp (IDE, CAD, đồ hoạ), Unity game chọn bản, enterprise desktop |
| **Hoàn tác cho người dùng** | Restore từ snapshot | Chạy lại `.exe` bản cũ — tức thì, không cần framework |
| **Rủi ro khi migration hỏng** | Có (được che bằng snapshot + journal) | Gần như không — thư mục cũ chưa từng được mở để ghi |
| **Đĩa** | Tiết kiệm | Tốn |

Điểm quan trọng của side-by-side mà bản 2.1 chưa nêu: **thư mục version mới chỉ tồn tại sau một
lệnh rename duy nhất ở bước commit**. Trước đó nó hoàn toàn không có mặt trên đĩa — vì một thư
mục version đã tồn tại là thư mục mà lần khởi động sau sẽ tin tưởng.

Và: `RequiresRunWithEmptyPlan()`. Với side-by-side, ship 2.0 mà **không** có step nào vẫn phải
clone dữ liệu 1.0 sang 2.0 — nếu không, 2.0 khởi động với thư mục rỗng. In-place trả `false`,
side-by-side trả `true` khi thư mục đích chưa có. Engine không cần biết chi tiết đó.

---

## 5. Vòng đời một lần chạy

```
RunAsync()
  │
  ├─ 0. options.Validate()          ← path guard, disjoint check, số học
  │
  ├─ 1. IMigrationLock.TryAcquire() ── null ──▶ Blocked (tiến trình khác đang migrate)
  │
  ├─ 2. Journal.Read()  ── có ──▶ strategy.RecoverAsync()   [Recovering]
  │                               └─ Phase == Preparing → xoá snapshot dở, dữ liệu chưa hề đổi
  │                               └─ ngược lại          → Replace(working ← snapshot)
  │
  ├─ 3. strategy.ResolveCurrentData()                        [Planning]
  │     └─ current > target && !AllowDowngrade ──▶ Blocked
  │
  ├─ 4. MigrationPlan.Create()  ── MigrationPlanException ──▶ Blocked
  │     └─ rỗng && !RequiresRunWithEmptyPlan ──▶ UpToDate (đóng dấu nếu chưa có dấu)
  │
  ├─ 5. ManualInteractive → IUpdatePromptService.ConfirmAsync()
  │     └─ từ chối ──▶ Deferred (CanContinue = AllowRunningOnOlderData)
  │
  ├─ 6. Journal.Write(Preparing)   ◀── từ đây trở đi mọi cú chết đều recover được
  │     strategy.PrepareAsync()                              [Preparing  0→20%]
  │
  ├─ 7. Journal.Write(Migrating)
  │     foreach step / foreach provider (tuần tự, không song song)
  │         provider.UpAsync() / DownAsync()                 [Migrating 20→97%]
  │         Journal.Write(lastCompletedStepId)
  │
  ├─ 8. Journal.Write(Committing)
  │     strategy.CommitAsync()  ← không nhận CancellationToken
  │     Journal.Clear()                                      [Committing 97→100%]
  │     ▶ Succeeded
  │
  └─ catch ─▶ RollBackAsync()  ← nằm NGOÀI khối catch gốc     [RollingBack]
              ├─ thành công ─▶ Canceled | Failed  (dữ liệu nguyên trạng)
              └─ thất bại   ─▶ RollbackFailed     (giữ journal + snapshot, CanContinue=false)
```

Chi tiết dễ bỏ sót: rollback nằm **ngoài** khối `catch` bắt lỗi provider. Nếu để bên trong, lỗi
của chính rollback sẽ bị `catch (Exception)` phía sau bắt mất, và `CancellationToken` — vốn đã
bị cancel — có thể lọt vào làm hỏng thao tác khôi phục.

### `MigrationOutcome`

| Outcome | Dữ liệu | `CanContinue` |
|---|---|---|
| `UpToDate` | không đổi, đã đúng version | `true` |
| `Succeeded` | đã lên version mới | `true` |
| `Canceled` | khôi phục nguyên trạng | `AllowRunningOnOlderData` |
| `Deferred` | không đụng tới | `AllowRunningOnOlderData` |
| `Failed` | khôi phục nguyên trạng | `AllowRunningOnOlderData` |
| `RollbackFailed` | **có thể không nhất quán** | `false` — luôn luôn |
| `Blocked` | không đụng tới | `false` |

---

## 6. Crash-safety: journal + atomic swap

### 6.1. Atomic swap

```csharp
DirectoryOperations.Replace(target, replacement, discard):
    Delete(discard)
    if exists(target):  Move(target → discard)      // rename, atomic
    Move(replacement → target)                       // rename, atomic
    TryDelete(discard)
```

Mọi trạng thái mà một cú crash có thể để lại đều khôi phục được:

| Chết ở đâu | Trên đĩa còn gì | Recovery làm gì |
|---|---|---|
| trước rename 1 | `target` đầy đủ | không cần làm gì |
| giữa rename 1 và 2 | `discard` đầy đủ, `target` không có | rename `discard` về `target` |
| sau rename 2 | `target` đầy đủ (đã là bản mới) | xoá `discard` |

Nếu `Move` thất bại (khác volume), tự động fallback sang copy + delete; và nếu rename 2 ném lỗi
thì `discard` được đưa về chỗ cũ trước khi rethrow.

### 6.2. Journal

Nằm ở **backup root**, không nằm trong thư mục dữ liệu — vì nhiệm vụ của nó chính là sống sót
qua các thao tác thay thế thư mục đó.

```
# Barbatos.Migration in-flight run. If this file exists at startup, the previous migration did not finish.
sessionId=20260727143012481
startedUtc=2026-07-27T14:30:12.4810000+00:00
model=InPlaceSingleFolder
direction=Upgrade
fromVersion=1.0.0
toVersion=2.0.0
originalDirectory=C:\Users\...\AppData\Local\Acme\MyApp\Data
workingDirectory=C:\Users\...\AppData\Local\Acme\MyApp\Data
backupDirectory=C:\Users\...\AppData\Local\Acme\MyApp\.migration\snapshot-20260727143012481
phase=Migrating
lastCompletedStepId=1.5.0
```

`phase` là thứ quyết định recovery:

- `Preparing` → snapshot chưa xong, **nhưng cũng chưa provider nào chạy** → dữ liệu vẫn đúng
  nguyên trạng. Xoá snapshot dở, không restore. (Restore ở đây sẽ là hành động **sai**.)
- `Migrating` / `Committing` / `RollingBack` → snapshot hoàn chỉnh → `Replace()` khôi phục.

Journal không parse được thì coi như không có: hành động dựa trên field không tin cậy nguy hiểm
hơn là để phép so sánh version quyết định.

---

## 7. Core contracts

```csharp
namespace Barbatos.Migration;

public interface IMigrationContext
{
    Version   CurrentDataVersion { get; }
    Version   TargetDataVersion  { get; }
    MigrationDirection Direction { get; }
    InstallationModel  Model     { get; }

    string  WorkingDirectory  { get; }   // provider ĐỌC & GHI ở đây
    string  OriginalDirectory { get; }   // side-by-side: bản cũ, CHỈ ĐỌC
    string? BackupDirectory   { get; }   // null khi strategy không chụp snapshot

    IMigrationLogger Logger { get; }
    IDictionary<string, object?> Items { get; }   // truyền state giữa các provider

    string GetWorkingPath(string relativePath);
}

public interface IMigrationProvider
{
    string Name    { get; }
    double Weight  { get; }   // trọng số progress, mặc định 1.0
    bool   CanDown { get; }   // kiểm tra trước khi động vào dữ liệu

    Task UpAsync  (IMigrationContext c, IProgress<MigrationProgress>? p, CancellationToken ct);
    Task DownAsync(IMigrationContext c, IProgress<MigrationProgress>? p, CancellationToken ct);
}

public interface IMigrationStep
{
    string  Id            { get; }   // ổn định, ghi vào ledger — KHÔNG đổi sau khi ship
    Version TargetVersion { get; }
    string  Description   { get; }
    IReadOnlyList<IMigrationProvider> Providers { get; }
}

public interface IInstallationStrategy
{
    InstallationModel Model { get; }

    DataLocation ResolveCurrentData();
    bool RequiresRunWithEmptyPlan(DataLocation currentData);

    Task PrepareAsync (MigrationContext c, IProgress<MigrationProgress>? p, CancellationToken ct);
    Task CommitAsync  (MigrationContext c, IReadOnlyList<string> appliedStepIds, IProgress<MigrationProgress>? p);
    Task RollbackAsync(MigrationContext c, Exception? error, IProgress<MigrationProgress>? p);
    Task RecoverAsync (MigrationJournalEntry journal, IProgress<MigrationProgress>? p);
}
```

**Yêu cầu với provider** (viết rõ vì đây là chỗ dev dễ sai):

1. Chỉ đọc/ghi dưới `WorkingDirectory`. **Không bao giờ** đụng `OriginalDirectory` hay
   `BackupDirectory`. Hardcode đường dẫn dữ liệu thay vì dùng `WorkingDirectory` là cách dễ nhất
   để phá hỏng bản mà người dùng còn có thể lùi về.
2. Kiểm tra `cancellationToken` trong **mọi** vòng lặp.
3. **Phải chạy lại được.** Crash sau khi provider xong nhưng trước khi commit → snapshot được
   khôi phục và cả step chạy lại từ đầu ở lần sau.
4. Provider **không** cần tự atomic hay tự đảo được — strategy lo phần đó.

Các provider trong một step chạy **tuần tự, không song song**. Hai provider cùng ghi một thư
mục chính là loại hỏng dữ liệu mà framework này sinh ra để ngăn.

---

## 8. Progress & cancellation

### Phân bổ phần trăm

| Phase | Dải | Huỷ được? |
|---|---|---|
| `Planning` | 0 | — |
| `Recovering` | 0–100 (độc lập) | không |
| `Preparing` | 0–20 | **có** |
| `Migrating` | 20–97, chia theo `Weight` | **có** |
| `Committing` | 97–100 | không |
| `RollingBack` | 0–100 (độc lập) | không |
| `Completed` | 100 | — |

Trong `Migrating`, mỗi provider chiếm `Weight / Σ Weight × 77%`.

### Ba quyết định về threading

1. **Relay đồng bộ.** `ProgressRelay` không kế thừa `Progress<T>` — thứ đó post qua
   `SynchronizationContext`, làm report đến bất đồng bộ và có thể sai thứ tự. Marshal về UI là
   việc của caller, làm **một lần ở biên**.
2. **Đơn điệu tăng.** High-water mark trong relay.
3. **Chạy off-UI-thread.** `MigrationRunner` (WPF) bọc `Task.Run` vì copy thư mục là I/O đồng bộ.

### Cancellation

`CancellationToken` được tôn trọng trong `Preparing` và `Migrating`. Từ `Committing` trở đi bị
bỏ qua — bỏ dở ở đó tốn kém hơn là làm nốt. `MigrationProgressViewModel.CanCancel` phản ánh
đúng điều này để nút Cancel tự xám đi, thay vì nhận click rồi không làm gì.

---

## 9. Update trigger mode: Silent vs Manual

Bản 2.1 gộp app-update và data-migration làm một. Tách ra:

- **Tải binary mới** — hoãn lúc nào cũng an toàn. **Không thuộc phạm vi framework này.**
- **Chuyển đổi dữ liệu** — binary mới *đã đang chạy*. Hoãn nghĩa là chạy code mới trên schema cũ.

| | `SilentAutoUpdate` | `ManualInteractive` |
|---|---|---|
| Khi nào | Ngay lúc khởi động, có progress, không hỏi | Hỏi qua `IUpdatePromptService` trước |
| Hợp với | Mobile, SaaS, app nhẹ (migration 1–2 giây) | CAD, công nghiệp, app mà người dùng mở lên để làm cho xong việc gấp |
| Hoãn được? | Không | Chỉ khi `AllowRunningOnOlderData = true` |

`MigrationPromptContext` cung cấp cho UI: `Plan` (hiển thị `Describe()` cho power user), `CanDefer`,
`EstimatedDataSizeBytes` (để nói "sẽ mất vài phút" khi đúng là như vậy), `ReleaseNotes`.

**`CanDefer = false`** nghĩa là app đã tuyên bố không chạy được trên dữ liệu cũ. Dialog khi đó
phải nói rõ lựa chọn là "migrate hoặc đóng app", và **không được** hiện nút "Nhắc lại sau" dẫn
đến ngõ cụt.

---

## 10. Tích hợp Barbatos.Wpf.Core

```csharp
var builder = WpfApp.CreateBuilder();

builder.ConfigureMigration(options =>
       {
           options.Model = InstallationModel.InPlaceSingleFolder;
           options.BackupRetentionCount = 2;
       })
       .AddStep("1.5.0", "Chuyển settings sang cấu trúc mới",
           new JsonMigrationProvider("settings.json",
               json => json.MoveIntoSection("fontSize", "editor")))
       .AddStep("2.0.0", "Tách bảng người dùng",
           new SqliteMigrationProvider("app.db", [
               "ALTER TABLE Users RENAME TO Users_old;",
               "CREATE TABLE Users (Id INTEGER PRIMARY KEY, Name TEXT, Email TEXT);",
               "INSERT INTO Users (Id, Name, Email) SELECT Id, Name, Email FROM Users_old;",
               "DROP TABLE Users_old;",
           ]),
           new FileSystemMigrationProvider("Sắp xếp lại assets", ops => ops
               .EnsureDirectory("assets")
               .MoveDirectory("images", "assets/images")));
```

### Mặc định lấy từ đâu

| Option | Nguồn | Vì sao |
|---|---|---|
| `DataDirectory` | `IFileSystem.AppDataDirectory` | Đúng thư mục `<Publisher>/<AppGuid>/Data` mà phần còn lại của Barbatos.Wpf đang ghi vào — engine bảo vệ đúng dữ liệu app thật sự dùng |
| `TargetDataVersion` | `IAppInfo.Version` | Ship build mới là step tới hạn, không phải khai báo thêm |
| `InitialDataVersion` | `IVersionTracking.VersionHistory` | Xem dưới |
| `Logger` | `ILogger` qua adapter | Log migration nằm cùng chỗ với mọi log khác — nơi người điều tra sự cố mất dữ liệu sẽ tìm đầu tiên |

### `IVersionTracking` — phần đáng giá nhất của tích hợp

Với dữ liệu **chưa có dấu version**, mặc định `0.0.0.0` sẽ chạy lại toàn bộ step trên dữ liệu
thật của một bản cài đã dùng lâu — tốn thời gian, và tệ hơn là có thể chạy lại một step không an
toàn khi lặp.

`VersionTracking` đã ghi sẵn lịch sử app version chạy trên máy này **từ trước khi có framework
migration**. Version mới nhất trong lịch sử mà nhỏ hơn build hiện tại chính là version đã ghi
dữ liệu đó:

```csharp
private static Version ResolveInitialDataVersion(IAppInfo appInfo, IVersionTracking versionTracking)
{
    Version current = appInfo.Version;
    Version? newestOlder = null;

    foreach (string entry in versionTracking.VersionHistory)
    {
        if (!Version.TryParse(entry, out Version? parsed) || parsed >= current)
            continue;
        if (newestOlder == null || parsed > newestOlder)
            newestOlder = parsed;
    }

    return newestOlder ?? current;   // chưa build cũ nào chạy ở đây → cài mới, không có gì để migrate
}
```

**Vì sao đọc `VersionHistory` chứ không phải `PreviousVersion`:** lúc migration chạy, version
tracking **đã** ghi nhận build mới rồi. Nếu lần thử đầu bị huỷ và người dùng mở lại app,
`PreviousVersion` khi đó đã thành build mới → heuristic sẽ báo "không có gì để migrate" và bỏ
qua toàn bộ. `VersionHistory` vẫn còn version cũ. Đây là loại lỗi chỉ lộ ra ở lần thử thứ hai —
đúng loại khó nhất để phát hiện lúc test.

Khi dữ liệu đã có dấu version thì dấu luôn thắng; heuristic chỉ ảnh hưởng đúng lần migrate đầu.

### Khởi động

```csharp
protected override async void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);

    var progress = Services.GetRequiredService<MigrationProgressViewModel>();
    SplashViewModel.Migration = progress;      // splash screen bind vào đây

    var result = await Services.GetRequiredService<IMigrationRunner>()
        .RunAsync(progress, progress.CancellationToken);

    await CloseSplashScreenAsync();

    if (!result.CanContinue)
    {
        MessageBox.Show(result.Outcome == MigrationOutcome.RollbackFailed
            ? $"Không thể khôi phục dữ liệu tự động. Bản sao nằm ở {result.BackupDirectory}."
            : "Cập nhật chưa hoàn tất nên ứng dụng không thể khởi động.");
        Shutdown(1);
        return;
    }

    new MainWindow().Show();
}
```

Vị trí gọi rất quan trọng: **sau `base.OnStartup(e)` (host đã dựng xong, splash đang hiện) và
trước khi bất cứ thứ gì mở dữ liệu ra.**

---

## 11. Cấu trúc solution & package

Đổi tên so với bản 2.1 để khớp quy ước các repo Barbatos khác (`Barbatos.i18n.Json`,
`Barbatos.i18n.Wpf`, `Barbatos.Wpf.Core`) — ngắn hơn và nhất quán hơn `Providers.*` / `Hosting.*`:

```
Barbatos.Migration.slnx                          (có folder "Solution Items" gom file gốc)
 ├── README.md                                   một tài liệu hướng dẫn cho cả family
 ├── API-REFERENCE.md                            một tham chiếu API cho cả family
 ├── ThirdPartyNotices.txt
 ├── build/nuget.png                             một logo dùng chung cho mọi package
 ├── .github/
 │    ├── workflows/…-ci.yml                     build + test + pack mỗi push/PR
 │    ├── workflows/…-cd-nuget.yml               chạy test rồi mới push NuGet khi publish release
 │    ├── dependabot.yml
 │    └── FUNDING.yml
 ├── src/                                        tất cả: net10.0; net9.0; net8.0
 │   ├── Barbatos.Migration.Core                 zero-dependency
 │   │      engine · plan · journal · lock · version store · 2 installation strategy
 │   ├── Barbatos.Migration.Database             zero-dependency (System.Data.Common in-box)
 │   ├── Barbatos.Migration.EntityFrameworkCore  + Microsoft.EntityFrameworkCore.Relational
 │   ├── Barbatos.Migration.Json                 zero-dependency (System.Text.Json in-box)
 │   ├── Barbatos.Migration.Ini                  zero-dependency
 │   ├── Barbatos.Migration.Csv                  zero-dependency
 │   ├── Barbatos.Migration.FileSystem           zero-dependency
 │   ├── Barbatos.Migration.DependencyInjection  + Microsoft.Extensions.*
 │   └── Barbatos.Migration.Wpf                  + Barbatos.Wpf.Core   net10/9/8-windows
 │          (tương lai: .Maui, .Unity)
 ├── samples/
 │   └── Barbatos.Migration.Wpf.Sample           playground upgrade/downgrade/fail/cancel
 │        └── Migrations/                        một step một file, quét bằng AddStepsFromAssembly
 └── tests/
     └── Barbatos.Migration.Core.UnitTests
```

Tài liệu và logo gom về một chỗ, theo đúng cách `Barbatos.i18n` làm: một `README.md`, một
`API-REFERENCE.md`, một `build/nuget.png` mà mọi package cùng dùng làm `PackageIcon`. Chín file
README song song sẽ lặp lại nhau và trôi lệch nhau ngay sau vài lần sửa.

Chỉ target `net8.0` trở lên. Bản 3.0 có đa target xuống `netstandard2.0/2.1` để phủ Unity và
.NET Framework, nhưng cái giá là polyfill (`IsExternalInit`,
`DynamicallyAccessedMembersAttribute`), tránh `ValueTask`/`IAsyncDisposable`, và một loạt
`#if`. Bỏ đi thì code sạch hơn nhiều và dùng được `DbConnection.BeginTransactionAsync`,
`DisposeAsync`, collection expression thoải mái. **Hệ quả:** Unity chỉ dùng được khi
runtime CoreCLR (.NET 8+) của nó chính thức ra — Mono/IL2CPP hiện tại không cài được package
`net8.0`. Xem §13.

Core dùng được **không cần DI container** qua `MigrationEngineBuilder` — Unity và tiện ích nhỏ
không có `IServiceCollection`, và dùng container chỉ để cấu hình một singleton là ngược đời.
Sample dùng cả hai đường: `App.xaml.cs` đi qua host, `MainViewModel.cs` đi qua builder.

### 11.1. Vì sao tách `Database` và `EntityFrameworkCore`

Phần **cơ chế** của migration CSDL — open, begin, chạy statement, verify, commit, close — giống
hệt nhau ở mọi engine, nên nó thuộc về `DatabaseMigrationProvider` và không cần biết driver nào.
Phần **khác nhau** gom vào `IDatabaseDialect`:

| Dialect | DDL trong transaction? | Tắt foreign key bằng | Kiểm tra toàn vẹn |
|---|---|---|---|
| `Sqlite` | có | `PRAGMA foreign_keys = OFF` | `PRAGMA foreign_key_check` |
| `SqlServer` | có | `NOCHECK CONSTRAINT ALL` | `WITH CHECK CHECK CONSTRAINT ALL` khi bật lại |
| `PostgreSql` | có | *(deferred constraints)* | `SET CONSTRAINTS ALL IMMEDIATE` |
| `MySql` | **không** | `SET FOREIGN_KEY_CHECKS = 0` | — |
| `Generic` | giả định có | — | — |

MySQL commit DDL ngầm — một step hỏng ở statement thứ tư để lại ba statement đầu đã áp. Provider
**log warning** khi gặp dialect như vậy, vì hứa atomicity mà không làm được thì tệ hơn là nói
thẳng.

`SqliteDialect` là dialect duy nhất thực sự "nặng", và đúng những chỗ mà bản 2.1 sai: checkpoint
WAL trước khi nhả connection, và gọi `ClearAllPools()` **qua reflection** trên type thật của
connection — giữ package không phụ thuộc driver mà vẫn đảm bảo file handle đã đóng trước khi
engine rename thư mục.

`Barbatos.Migration.EntityFrameworkCore` trả lời trực tiếp "EF Core hỗ trợ rất nhiều loại DB":
`EfCoreMigrationsProvider<TContext>` chạy `Database.MigrateAsync()` **bên trong snapshot** của
engine. EF Core rất giỏi đổi schema và hoàn toàn không có khái niệm hoàn tác; cái nó thiếu —
snapshot, journal chống crash, rollback, progress, và thứ tự chung với các step JSON/file — thì
engine này có sẵn. Kèm `DbContextMigrationProvider<TContext>` cho phần biến đổi **dữ liệu** viết
bằng LINQ trên entity, thứ mà migration file của EF Core làm rất vụng.

**Vì sao không gộp `Database` vào `EntityFrameworkCore`?** Đúng là EF Core chạy được raw SQL qua
`ExecuteSqlRaw`, nên nhìn qua thì hai package chồng nhau. Nhưng gộp lại sẽ mất ba thứ:

1. **Dependency.** App không dùng ORM sẽ phải kéo `Microsoft.EntityFrameworkCore.Relational` +
   một provider chỉ để chạy 4 câu `ALTER TABLE`. `Database` không phụ thuộc driver nào cả.
2. **`IDatabaseDialect`.** Tắt/bật foreign key theo từng engine, kiểm tra toàn vẹn **bên trong**
   transaction, cảnh báo khi DDL không transactional — không phải việc của EF Core.
3. **Nhả file handle.** Đây mới là điểm quyết định, và trong lúc rà lại chính câu hỏi này em
   phát hiện **package EF Core đang có đúng lỗ hổng đó**: `DbContext.DisposeAsync()` trả
   connection về pool của driver chứ **không đóng file**. Với SQLite thì file `.db` vẫn mở khi
   engine đi rename thư mục → rollback bị chặn bởi chính file nó đang khôi phục.

Nên hai package **không phải hai lựa chọn thay thế nhau, mà là hai lớp**:
`EntityFrameworkCore` giờ tham chiếu `Database` để dùng lại `IDatabaseDialect`, và cả
`EfCoreMigrationsProvider` lẫn `DbContextMigrationProvider` đều có property `Dialect` — đặt
`DatabaseDialects.Sqlite` cho DB dạng file thì WAL được checkpoint và pool được clear trong khối
`finally`, trên **mọi** đường thoát kể cả khi lỗi (đường lỗi cần điều đó hơn cả đường thành công).

| | `Database` | `EntityFrameworkCore` |
|---|---|---|
| Dependency | không | EF Core Relational + provider |
| Viết gì | câu SQL | migration class sẵn có của EF, và LINQ trên entity |
| Hợp khi | app dùng ADO.NET thuần, hoặc không có ORM | app đã dùng EF Core |
| Vị trí lỗi theo từng statement | có | EF Core tự báo |
| Dialect | có | có — dùng chung |

> Phạm vi lời hứa: snapshot chỉ bảo vệ dữ liệu **nằm trong thư mục dữ liệu**. DB file-backed
> (SQLite, LiteDB) thì trọn vẹn; DB trên server thì không — ở đó chỉ còn transaction, và tính
> transactional của DDL thì tuỳ engine.

### 11.2. Khai báo step: một step một file

Chuỗi `AddStep(...)` ổn với step hai dòng, và không đọc nổi khi một trong số đó dài hai trăm
dòng. Nên step tự khai báo bằng attribute, và được tìm bằng cách quét assembly:

```csharp
// Migrations/RebuildSearchIndex.cs — một file, một step, dài bao nhiêu cũng được.
[MigrationStep("2.0.0", "Rebuild the search index")]
public sealed class RebuildSearchIndex : CodeMigrationStep
{
    public override double Weight => 8.0;

    public override async Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken ct)
    {
        // ...
    }
}
```

```csharp
builder.ConfigureMigration().AddStepsFromAssembly();
```

Ba lớp, tuỳ độ phức tạp của step:

| Kiểu | Khi nào |
|---|---|
| `MigrationStep` (class có sẵn) | đăng ký inline, step ngắn |
| `MigrationStepBase` | step gom nhiều provider — override `CreateProviders()` |
| `CodeMigrationStep` | step **là** một khối logic — override thẳng `UpAsync`; step tự đóng vai provider duy nhất của nó |

Chi tiết đáng lưu ý:

- **Thứ tự quét là deterministic.** `Assembly.GetTypes()` trả về theo thứ tự metadata, không đảm
  bảo ổn định giữa các lần build. Scanner sort theo version rồi tới id — "thứ tự step chạy" không
  phải thứ để phó mặc may rủi. (Engine cũng sort lại, nên đây là lớp bảo vệ thứ hai.)
- **`Providers` build lazy, đúng một lần.** Step không nằm trong plan thì không bao giờ construct
  provider của nó — quan trọng khi provider mở connection hay đọc file chỉ để tồn tại.
- **`Id` mặc định là tên class**, mà id chính là thứ ledger ghi lại → **đổi tên class đã ship là
  đổi danh tính step**. Đặt `[MigrationStep(..., Id = "...")]` nếu có ý định tổ chức lại.
- **DI đăng ký *type*, không construct.** `MigrationBuilder.AddStepsFromAssembly()` gọi
  `services.AddSingleton(typeof(IMigrationStep), type)`, nên step nhận được constructor injection
  như mọi service khác. Bản không-DI thì cần constructor public không tham số, và báo lỗi rõ ràng
  nếu thiếu.
- **`ReflectionTypeLoadException` được nuốt có kiểm soát**: trả về các type load được. Một package
  tuỳ chọn không tham chiếu mà làm cả assembly ném lỗi sẽ biến thành "không tìm thấy migration
  nào" — và migration im lặng không chạy là kiểu hỏng đáng tránh nhất.
- Quét dùng reflection nên các method gắn `[RequiresUnreferencedCode]`. Publish trimmed thì dùng
  `AddStep` từng cái.
- **Tên attribute là `MigrationStepAttribute`, không phải `MigrationAttribute`.** EF Core cũng có
  `MigrationAttribute` (`Microsoft.EntityFrameworkCore.Migrations`), mà chính framework này lại
  khuyến khích dùng EF Core migration — trùng tên thì mọi chỗ dùng đều phải viết đầy đủ namespace.
  Phát hiện ra khi viết test cho package EF Core: file test không compile được.

### 11.3. Provider file: nguyên tắc "format-preserving"

Ba package `Json`, `Ini`, `Csv` đều theo cùng một nguyên tắc, và nó khác hẳn cách một thư viện
đọc file thông thường làm việc.

Cách hiển nhiên để migrate một file settings là parse thành dictionary, sửa dictionary, ghi lại.
Với **đọc** thì đúng; với **migrate** thì sai, vì file ghi ra đã mất sạch comment người dùng viết,
mất dòng trống phân nhóm, mất thứ tự key, mất quy ước spacing. Người dùng mở lại `config.ini`
đầy chú thích của mình sau khi update và thấy nó thành một danh sách phẳng xếp theo alphabet —
với họ, bản update đó đã **phá file của họ**.

> Đây chính là điểm học được khi so với `Barbatos.i18n`: parser bên đó (`IniLocalizationParser`,
> `CsvLocalizationParser`) chỉ cần **một chiều** — đọc ra `Dictionary<LocalizationKey, string>`
> rồi thôi, nên parse-thành-dictionary là thiết kế đúng ở đó. Migration cần **round-trip**, nên
> phải là một document model giữ nguyên định dạng. Cùng format, khác bài toán, khác kiến trúc.

Cụ thể ở từng package:

| | Giữ nguyên | Cách làm |
|---|---|---|
| `Json` | key không nhận ra (do plugin, do bản mới hơn ghi) | thao tác trên `JsonNode` DOM thay vì model có kiểu |
| `Ini` | comment, dòng trống, thứ tự key, indentation, spacing quanh `=`, comment cuối dòng | `IniDocument` giữ file như **danh sách dòng**; mỗi dòng phân loại một lần, dòng nào không bị sửa thì ghi lại **nguyên văn**; sửa value chỉ viết lại đúng phần value của đúng dòng đó |
| `Csv` | delimiter, kiểu quoting, line ending, có/không header | phát hiện khi parse, tái tạo khi ghi; nhớ từng ô có được quote hay không |

Cả ba đều: ghi atomic qua `AtomicFile` (temp → flush to disk → rename), giữ nguyên encoding gốc
(kể cả BOM), và **mọi thao tác là no-op khi key/column không tồn tại** — vì một step chạy dở rồi
crash sẽ được chạy lại từ snapshot, nên `RenameKey` với key đã đổi tên rồi không được phép ném
lỗi.

Điểm khác biệt cuối: file hỏng thì **từ chối**, không đoán. CSV có dấu nháy không đóng khiến toàn
bộ phần còn lại thành một ô khổng lồ — parser dễ dãi sẽ nhận, rồi migration ghi đè và dữ liệu
người dùng biến mất. `CsvReader` ném lỗi kèm số dòng, engine restore snapshot, file gốc còn
nguyên cho người dùng xem.

---

## 11.4. Ba lỗi khả năng mở rộng do viết test mà lộ ra

`IInstallationStrategy` và `IDatabaseDialect` là public để bên thứ ba viết được. Nhưng đến khi
thật sự viết một implementation **ngoài assembly** trong test thì mới lộ ra là không viết nổi:

| Lỗi | Triệu chứng | Cách sửa |
|---|---|---|
| `MigrationContext.WorkingDirectory` / `BackupDirectory` là `internal set` | Strategy bên ngoài không trỏ được working directory → không thể implement side-by-side kiểu khác | Thêm `SetWorkingDirectory()` / `SetBackupDirectory()` public. Là **method** chứ không phải property setter, để một provider gọi nhầm thì nhìn thấy ngay khi review |
| `MigrationProgress` không có constructor nhận `MigrationPhase` | Strategy bên ngoài không báo được phase `Preparing`/`RollingBack` → UI không biết lúc nào phải tắt nút Cancel | Thêm ctor `MigrationProgress(MigrationPhase, double, string?, bool)` public |
| `[Migration]` trùng tên với EF Core | Project dùng cả hai không compile | Đổi thành `[MigrationStep]` |

Bài học: **interface public không có nghĩa là mở rộng được.** Chỉ khi viết một implementation
thật ở ngoài assembly mới biết. Ba lỗi này đều nằm im cho tới lúc có test.

---

## 12. Kiểm chứng bằng test

38 test, tất cả đều pass. Những cái quan trọng nhất chứng minh đúng các bảo đảm an toàn:

| Test | Chứng minh |
|---|---|
| `A_failing_step_restores_the_data_exactly_as_it_was` | Sau khi step 2 ném lỗi, file bị step 1 ghi đè trở về nội dung gốc, file bị xoá quay lại, rác bị dọn, dấu version vẫn ở 1.0.0 |
| `Cancelling_mid_step_restores_the_data_and_reports_Canceled` | Huỷ giữa chừng ≠ lỗi: `Error == null`, dữ liệu nguyên trạng |
| `A_run_killed_before_it_finished_is_recovered_on_the_next_launch` | Dựng lại đúng hiện trường process bị kill (snapshot + journal `Migrating` + dữ liệu nửa vời) → lần sau khôi phục rồi chạy lại thành công |
| `A_run_killed_during_preparation_leaves_the_data_untouched` | Journal `Preparing` → **không** restore từ snapshot dở |
| `A_second_process_is_blocked_while_a_migration_is_running` | Khoá liên tiến trình hoạt động |
| `Data_newer_than_the_application_is_blocked_rather_than_silently_accepted` | Không âm thầm cho app đọc schema tương lai |
| `A_failure_leaves_no_new_version_directory_behind` (SxS) | Upgrade hỏng không tạo thư mục version mới; thư mục cũ nguyên vẹn từng byte |
| `A_version_with_no_steps_between_it_and_the_previous_one_still_gets_its_data` (SxS) | Ship 2.0 không có step vẫn phải clone dữ liệu |
| `An_abandoned_staging_clone_is_swept_up_on_the_next_launch` (SxS) | Upgrade crash lặp lại không làm đầy đĩa |
| `Progress_is_reported_monotonically_and_ends_at_100` | Progress bar không lùi |
| `A_backup_directory_nested_inside_the_data_directory_is_rejected` | Chặn cấu hình sai lúc khởi động |
| `Json_provider_rewrites_the_document_and_keeps_unknown_keys` | Key do plugin ghi sống sót qua migration |
| `Json_provider_fails_the_run_on_a_corrupt_file_rather_than_overwriting_it` | Không ghi đè file hỏng |
| `A_downgrade_runs_the_steps_backwards_when_it_is_allowed` | 2.0 được đảo trước 1.5 |
| `Create_rejects_a_downgrade_across_a_forward_only_provider` | Plan bất khả thi fail **trước** khi chụp snapshot |

---

## 13. Lộ trình MAUI & Unity

Core không có gì phụ thuộc nền tảng, nên hai package còn lại chỉ là lớp mỏng.

**`Barbatos.Migration.Maui`** — làm được ngay
- `DataDirectory` ← `FileSystem.AppDataDirectory`; `TargetDataVersion` ← `AppInfo.Version`
- `MauiAppBuilder.ConfigureMigration(...)` đối xứng với bản WPF
- Marshal progress qua `IDispatcher` của MAUI, throttle giống `MigrationRunner`
- Lưu ý iOS/Android: chỉ dùng `InPlaceSingleFolder` (sandbox không có khái niệm "chạy lại bản
  cũ"), và `RequiredFreeSpaceFactor` quan trọng hơn hẳn vì đĩa điện thoại hay đầy

**`Barbatos.Migration.Unity`** — **đang bị chặn bởi TFM**
- Sau khi thu hẹp xuống `net8.0` (§11), package không cài được vào Unity đang dùng Mono/IL2CPP
  với profile netstandard2.1. Ba lựa chọn khi tới lúc làm:
  1. **Chờ Unity CoreCLR** (.NET 8+, đang trong lộ trình Unity 6.x) — không phải làm gì cả.
  2. Thêm lại `netstandard2.1` **chỉ cho `Core`** (+ `Json`, `FileSystem`), kèm polyfill.
     Ba package đó không dùng gì ngoài `System.IO`/`System.Text.Json`, nên chi phí thấp;
     `Database` và `EntityFrameworkCore` thì không cần trên Unity.
  3. Copy nguồn `Core` vào một Unity package riêng.
- Khi làm: `DataDirectory` ← `Application.persistentDataPath`, `MigrationEngineBuilder`
  (không DI), adapter `IMigrationLogger` → `UnityEngine.Debug`, bọc `Task` thành `IEnumerator`
  cho coroutine.
- WebGL không có thread và không có file system thật → cần `SkipFreeSpaceCheck` và một
  `IInstallationStrategy` riêng trên IndexedDB. Đây chính là lý do interface đó là public.

---

## Phụ lục: bảng đối chiếu API 2.1 → 3.0

| Bản 2.1 | Bản 3.0 |
|---|---|
| `BarbatosMigrationEngine` | `MigrationEngine` |
| `RunAsync() → bool` | `RunAsync() → MigrationResult` (`Outcome` + `CanContinue`) |
| `IInstallationStrategyHandler` | `IInstallationStrategy` (+ `ResolveCurrentData`, `RecoverAsync`, `RequiresRunWithEmptyPlan`) |
| `IMigrationContext.WorkingDataDirectory` | `WorkingDirectory` + `OriginalDirectory` + `BackupDirectory` |
| `IMigrationProvider.ProviderName` | `Name` (+ `Weight`, `CanDown`) |
| `MigrationProgress` (class) | `MigrationProgress` (readonly struct, + `Phase`, `IsIndeterminate`) |
| `BarbatosUpdateManager` | gộp vào `MigrationEngine` + `IUpdatePromptService` |
| `IVersionDetector` | `IDataVersionStore` (đọc **và** ghi) |
| — | `IMigrationJournal`, `IMigrationLock`, `IMigrationLogger`, `MigrationPlan`, `IMigrationStep.Id` |
| `Barbatos.Migration.Providers.Sqlite` | `Barbatos.Migration.Database` (mọi ADO.NET provider) + `Barbatos.Migration.EntityFrameworkCore` |
| `SqliteMigrationProvider` | `DatabaseMigrationProvider` + `DatabaseDialects.Sqlite` |
| `Barbatos.Migration.Hosting.Wpf` | `Barbatos.Migration.Wpf` |
