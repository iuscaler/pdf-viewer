# PDFViewer

빠른 렌더링과 펜/형광펜 주석을 지원하는 Windows용 PDF 뷰어입니다.
Edge PDF 뷰어의 사용감을 기준으로 삼고, 2페이지 보기와 전체화면 보기를 더 강화하는 것을 목표로 합니다.

## 목표

| 구분 | 내용 |
|---|---|
| 성능 | Edge 수준의 렌더링 속도. 수백 페이지 문서도 스크롤과 확대/축소가 끊기지 않을 것 |
| 주석 | 펜(필압 지원), 형광펜(자유 곡선 / 텍스트 선택 기반), 지우개, 실행 취소/다시 실행 |
| 호환성 | 주석은 PDF 표준 주석으로 저장. Edge, Acrobat 등 다른 뷰어에서도 보여야 함 |
| 보기 방식 | 1페이지 / 2페이지 보기, 표지 따로 보기, 연속 스크롤 / 페이지 넘김, 좌→우 / 우→좌 |
| 전체화면 | 테두리 없는 전체화면, 프레젠테이션 모드 |

### 범위에서 제외 (초기 버전)
- PDF 내용 편집 (텍스트/이미지 수정, 페이지 재배열)
- 전자 서명, 암호화 설정
- 폼 입력 (렌더링은 하되 편집은 추후 검토)

## 기술 스택

| 항목 | 선택 | 이유 |
|---|---|---|
| 런타임 | .NET 10 (LTS) | 최신 LTS, 성능 개선 |
| UI | WPF | 성숙한 `InkCanvas`(펜 필압, 지우개), 풍부한 자료 |
| PDF 엔진 | PDFium (`bblanchon.PDFium.Win32` NuGet + 직접 작성한 `LibraryImport` 바인딩) | Chromium/Edge와 같은 엔진, BSD 계열 라이선스, 주석 API 제공 |
| MVVM | CommunityToolkit.Mvvm | 소스 생성기 기반의 가벼운 MVVM |
| 테스트 | xUnit | |

## 아키텍처

```
┌──────────────────────────────────────────────────────────┐
│ PDFViewer.App (WPF)                                      │
│  MainWindow ─ Toolbar / Sidebar(썸네일) / DocumentView    │
│  ViewModels ─ DocumentViewModel, ToolViewModel ...       │
│  Controls   ─ PdfDocumentView (가상화된 페이지 표시)       │
│               PageAnnotationLayer (InkCanvas 오버레이)     │
└───────────────▲──────────────────────────────────────────┘
                │
┌───────────────┴──────────────────────────────────────────┐
│ PDFViewer.Core                                           │
│  Pdf/        PdfiumThread, PdfDocument, PdfPage          │
│  Rendering/  RenderScheduler, BitmapCache                │
│  Layout/     LayoutEngine (1페이지/2페이지/표지/RTL)        │
│  Annotations/ 주석 모델, PDF 주석 변환기, Undo/Redo         │
└───────────────▲──────────────────────────────────────────┘
                │ P/Invoke
┌───────────────┴──────────────────────────────────────────┐
│ pdfium.dll (네이티브)                                     │
└──────────────────────────────────────────────────────────┘
```

### 1. PDFium 접근 계층 (`Pdf/`)

**PDFium은 스레드 안전하지 않습니다.** 모든 PDFium 호출은 전용 스레드 하나(`PdfiumThread`)에서만 실행합니다.

- `PdfiumThread`: 작업 큐를 가진 단일 스레드. `Task<T> InvokeAsync(Func<T>)` 형태로 호출합니다.
- `PdfDocument`: 문서 열기/닫기(`FPDF_LoadDocument`), 페이지 수, 페이지 크기 조회, 저장
- `PdfPage`: 페이지 핸들 관리(`FPDF_LoadPage`), 렌더링, 텍스트 정보(`FPDFText_*`), 주석(`FPDFAnnot_*`)
- 네이티브 핸들은 `SafeHandle` 또는 `IDisposable`로 감싸서 해제 누락을 방지합니다.

### 2. 렌더링 파이프라인 (`Rendering/`)

```
스크롤/확대 이벤트
  → LayoutEngine: 현재 보이는 페이지와 화면 좌표 계산
  → RenderScheduler: 렌더 요청 등록 (우선순위: 화면 내 > 앞뒤 미리 읽기)
  → PdfiumThread: FPDF_RenderPageBitmap (주석 포함 플래그 FPDF_ANNOT)
  → BitmapCache 저장 → UI 스레드에서 Frozen BitmapSource로 표시
```

- **보이는 페이지만 렌더링**하고, 앞뒤 1~2 화면 분량은 미리 렌더링합니다.
- **화면 밖으로 나간 요청은 취소**합니다 (`CancellationToken`). 빠르게 스크롤해도 큐가 쌓이지 않습니다.
- **단계적 화질**: 확대/축소 중에는 기존 비트맵을 늘려서 즉시 표시하고, 조작이 멈추면 고해상도로 다시 렌더링해서 교체합니다.
- **고배율 타일 렌더링**: 확대율이 높아 페이지 비트맵이 너무 커지면 (예: 4096px 초과) 페이지를 타일로 나누고 보이는 타일만 렌더링합니다.
- **BitmapCache**: 키는 `(페이지 번호, 배율 단계)`, LRU 방식, 메모리 상한(기본 512MB)을 둡니다.
- 썸네일은 별도의 저해상도 캐시를 사용하고 우선순위를 가장 낮게 둡니다.
- 모니터 DPI(`VisualTreeHelper.GetDpi`)를 반영해서 고DPI 화면에서도 선명하게 렌더링합니다.

### 3. 레이아웃 엔진 (`Layout/`)

페이지 크기 목록과 보기 설정을 입력받아 각 페이지가 놓일 사각형(문서 좌표)을 계산하는 순수 로직입니다. UI와 분리되어 있어 단위 테스트가 쉽습니다.

| 설정 | 값 |
|---|---|
| 페이지 배치 | 1페이지, 2페이지 |
| 표지 따로 보기 | 2페이지 보기에서 첫 페이지를 단독으로 배치 |
| 스크롤 방식 | 연속 스크롤, 페이지 단위 넘김 |
| 읽기 방향 | 좌→우, 우→좌 (만화, 세로쓰기 책) |
| 맞춤 | 너비 맞춤, 페이지 맞춤, 사용자 지정 배율 |
| 페이지 간격 | 기본 8px |

- 2페이지 보기에서 두 페이지의 크기가 다르면 높이를 기준으로 정렬하고 가운데에 맞춥니다.
- 보기 방식을 바꿔도 **현재 보고 있던 페이지를 유지**합니다.
- `PdfDocumentView`는 레이아웃 결과로 보이는 페이지만 시각 요소를 만드는 가상화 컨트롤입니다.

### 4. 주석 (`Annotations/`)

#### 도구

| 도구 | 동작 | PDF 저장 형식 |
|---|---|---|
| 펜 | 자유 곡선, 색상과 굵기 선택, 필압 반영 | Ink 주석 |
| 형광펜 (자유) | 반투명 굵은 선, 곱하기(Multiply) 합성 | Ink 주석 + 투명도 |
| 형광펜 (텍스트) | 텍스트를 드래그해서 선택한 영역에 칠함 | Highlight 주석 (QuadPoints) |
| 지우개 | 획 단위로 지움 | 해당 주석 삭제 |

#### 동작 방식
- 각 페이지 위에 `InkCanvas`를 겹쳐 두고, 펜이나 형광펜 도구가 선택됐을 때만 입력을 받습니다.
- 입력 중인 획은 WPF가 직접 그립니다. 획이 끝나면 **주석 모델**(페이지 좌표 기준)로 변환해서 저장하고, 해당 페이지를 다시 렌더링합니다.
- 화면 좌표와 PDF 페이지 좌표(포인트 단위, 원점이 왼쪽 아래) 사이의 변환은 `PageTransform`이 담당하며, 페이지 회전(`/Rotate`)도 반영합니다.
- 텍스트 형광펜은 `FPDFText_GetCharIndexAtPos`, `FPDFText_CountRects`, `FPDFText_GetRect`로 선택된 글자 영역을 구합니다.
- 모든 주석 변경은 명령(Command) 객체로 기록해서 실행 취소/다시 실행(Ctrl+Z / Ctrl+Y)을 지원합니다.

#### 저장
- 주석 생성은 `FPDFPage_CreateAnnot`, `FPDFAnnot_AddInkStroke`, `FPDFAnnot_AppendAttachmentPoints`, `FPDFAnnot_SetColor`를 사용합니다.
- 다른 뷰어와의 호환성과 필압 표현을 위해 **외관 스트림(Appearance Stream)을 직접 생성**해서 `FPDFAnnot_SetAP`로 설정합니다.
- 저장은 `FPDF_SaveAsCopy`를 사용합니다. 저장 중 오류가 나도 원본이 깨지지 않도록 임시 파일에 먼저 쓰고 성공하면 원본과 교체합니다.
- 저장하지 않은 변경이 있으면 창 제목에 `*`를 표시하고, 닫을 때 저장 여부를 묻습니다.

### 5. 전체화면

- **F11 전체화면**: `WindowStyle=None`, `WindowState=Maximized`로 전환하고 툴바와 사이드바를 숨깁니다. 마우스를 화면 위쪽으로 가져가면 툴바가 나타납니다.
- **프레젠테이션 모드 (F5)**: 검은 배경에 페이지 맞춤으로 표시하고, 현재 보기 방식(1페이지/2페이지)을 유지한 채 한 화면씩 넘깁니다.
- 전체화면에서도 펜과 형광펜 주석을 쓸 수 있습니다.

## 단축키

열기, 확대/축소, 맞춤, 보기 방식, 전체화면, 페이지 이동은 구현되어 있고, 저장과 주석 도구는 계획입니다.

| 키 | 기능 |
|---|---|
| Ctrl+O / Ctrl+S / Ctrl+Shift+S | 열기 / 저장 / 다른 이름으로 저장 |
| Ctrl+휠, Ctrl+= / Ctrl+- | 확대 / 축소 |
| Ctrl+0 / Ctrl+1 / Ctrl+2 | 페이지 맞춤 / 실제 크기 / 너비 맞춤 |
| Ctrl+4 / Ctrl+5 | 1페이지 보기 / 2페이지 보기 |
| F11 / F5 / Esc | 전체화면 / 프레젠테이션 / 나가기 |
| ← → PageUp PageDown Home End | 페이지 이동 |
| Space, Enter, 왼쪽 클릭 / Backspace, 오른쪽 클릭 | 프레젠테이션에서 다음 / 이전 페이지 |
| P / H / E / V | 펜 / 형광펜 / 지우개 / 선택(기본) 도구 |
| Ctrl+Z / Ctrl+Y | 실행 취소 / 다시 실행 |

## 프로젝트 구조

```
PDFViewer/
├─ PDFViewer.slnx
├─ src/
│  ├─ PDFViewer.App/          WPF 앱 (Views, ViewModels, Controls)
│  └─ PDFViewer.Core/         PDFium 바인딩, 렌더링, 레이아웃, 주석 모델 (WPF 비의존)
└─ tests/
   └─ PDFViewer.Core.Tests/   레이아웃 엔진, 캐시, PDFium 렌더링 테스트
```

진행 상황은 [구현내용.md](구현내용.md)에 단계별로 기록합니다.

## 개발 단계

1. **기본 뷰어**: PDF 열기, 연속 스크롤, 확대/축소, 렌더 스케줄러와 캐시
2. **보기 방식**: 1페이지 / 2페이지, 표지 따로 보기, 우→좌, 전체화면, 프레젠테이션 모드
3. **펜 주석**: InkCanvas 오버레이, 색상과 굵기, 지우개, 실행 취소
4. **형광펜**: 자유 형광펜, 텍스트 선택 기반 형광펜
5. **저장**: PDF 표준 주석과 외관 스트림 생성, 안전한 저장
6. **부가 기능**: 썸네일 사이드바, 텍스트 검색, 최근 파일, 마지막 읽은 위치 기억

## 빌드

.NET 10 SDK가 필요합니다.

```powershell
dotnet build
dotnet test
dotnet run --project src/PDFViewer.App -- "C:\경로\문서.pdf"
```

## 라이선스 참고

- PDFium: BSD-3-Clause 계열 (Apache 2.0 일부 포함). 배포 시 라이선스 고지를 포함해야 합니다.
