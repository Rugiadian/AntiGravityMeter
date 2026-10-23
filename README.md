# ⚡ AntiGravityMeter (안티그라비티 사용량 데스크톱 위젯)

Google Antigravity의 실제 모델 사용량(Weekly & 5-Hour Limit Quota)을 실시간으로 감시할 수 있는 **Windows 전용 초소형 플로팅 데스크톱 위젯**입니다.

---

## 🌟 주요 특징

### 1. 100% 공식 Antigravity API 실시간 연동
- **공식 CloudCode `retrieveUserQuotaSummary` 직접 연동**:
  - Antigravity 앱 및 CLI가 내부적으로 호출하는 공식 엔드포인트(`https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary`)를 직접 호출하여, 앱의 `Settings -> Models -> Models & Usage` 화면에 표시되는 수치와 **100% 완벽히 일치**합니다.
  - **Weekly Limit Remaining** (주간 잔여 한도 %, 리셋 잔여일 표시)
  - **Five Hour Limit Remaining** (5시간 잔여 한도 %, 리셋 잔여시간 표시)
- **동그라미 도넛 게이지 인포그래픽 (`CircularQuotaCard`)**:
  - 원형 링 게이지 & 큼직한 퍼센트(%) 표시
  - 잔여량에 따른 직관적 컬러 변화 (초록 70%+ / 주황 30%+ / 빨강 30% 미만)
- **모든 쿼터 그룹 표시**: `retrieveUserQuotaSummary`가 반환하는 모든 그룹(예: *Claude and GPT models*)의 Weekly / 5-Hour 한도를 그룹별 섹션으로 표시
  - 요약 API에 없는 그룹(예: *Gemini models*)은 `fetchAvailableModels`의 모델별 `quotaInfo`로 계산하여 표시 (그룹 이름에 마우스를 올리면 포함 모델 목록 툴팁)
- **모델별 사용량 (Per-Model Usage)**: `fetchAvailableModels` 기반으로 사용 가능한 모든 모델(Gemini, Claude, GPT-OSS 등)의 잔여량 바 / % / 리셋까지 남은 시간 표시
  - Low / Medium / High 등 effort 변형은 하나의 모델로 병합
  - 헤더의 **☰ 버튼**으로 목록 펼치기/접기 (위젯 높이 자동 조정, 하단 위치 고정)

### 2. 초소형 콤팩트 플로팅 위젯 디자인
- **군더더기 없는 극단적 심플함**:
  - 불필요한 고급 통계, DB/로그 스캔을 모두 제거하여 쿼터 게이지와 모델별 잔여량만 표시
  - 너비 360px 고정, 높이는 표시할 그룹/모델 수에 맞춰 자동 조정되어 작업 영역을 방해하지 않음
  - CPU/RAM 점유율 최소화 (단 520KB 단일 실행 파일)
- **편리한 조작**:
  - 위젯 본체 어디든 마우스 드래그로 자유롭게 이동
  - **📌 창 항상 위에 고정 (Always-on-Top)** 토글
  - **◐ 투명도 조절** (`95%` ➔ `80%` ➔ `60%`)
  - **🔄 실시간 쿼터 즉시 새로고침** 버튼
  - **시스템 트레이 최소화 (✕)** 및 트레이 아이콘 지원

---

## 🚀 실행 방법

### 즉시 실행
`Publish\AntiGravityMeter.exe`를 더블클릭하거나 `Run_Widget.bat`을 실행합니다.

```bash
# 직접 실행
G:\Git\AntiGravityMeter\Publish\AntiGravityMeter.exe

# 또는 배치 파일 실행
Run_Widget.bat
```

### 소스 코드 빌드
.NET 10 SDK 환경에서 아래 스크립트를 실행하면 자동으로 릴리즈 빌드 및 패키징이 수행됩니다:

```bash
build.bat
```

---

## 📁 디렉터리 구성

```
G:\Git\AntiGravityMeter\
├── AntiGravityMeter.csproj  # .NET 10 WinForms 프로젝트 설정
├── Program.cs                # 메인 엔트리포인트 및 콘솔 테스트 모드
├── Models.cs                 # 쿼터 메트릭 데이터 구조체 정의
├── CircularQuotaCard.cs      # 원형 도넛 링 게이지 인포그래픽 커스텀 컨트롤
├── ModelUsageRow.cs          # 모델별 사용량 한 줄 바(bar) 커스텀 컨트롤
├── AgyCollector.cs           # 공식 retrieveUserQuotaSummary 실시간 수집 엔진
├── WidgetForm.cs             # 미려한 다크 테마 커스텀 플로팅 위젯 UI (높이 자동 조정)
├── build.bat                 # 릴리즈 빌드 배치 스크립트
├── Run_Widget.bat            # 위젯 즉시 실행 배치 파일
├── README.md                 # 본 설명 문서
└── Publish\                  # 즉시 실행 가능한 단일 바이너리 배포 폴더
    └── AntiGravityMeter.exe  # 단일 실행 파일 (520KB)
```
