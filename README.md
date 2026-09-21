# ⚡ AntiGravityMeter (안티그라비티 사용량 데스크톱 위젯)

Google Antigravity의 실제 모델 사용량(Weekly & 5-Hour Limit Quota), 호출 통계, 활성 세션 상태 및 리소스 점유율을 실시간으로 감시할 수 있는 **Windows 전용 초소형 플로팅 데스크톱 위젯**입니다.

---

## 🌟 주요 기능

### 1. 100% 공식 Antigravity 앱 연동 쿼터 인포그래픽
- **공식 Antigravity Language Server 직접 연동**:
  - Antigravity 앱의 `Settings -> Models -> Models & Usage` 화면에 표시되는 공식 쿼터 수치와 **100% 완벽히 일치**합니다.
  - **Weekly Limit Remaining** (주간 잔여 한도 %, 리셋 잔여일 표시)
  - **Five Hour Limit Remaining** (5시간 잔여 한도 %, 리셋 잔여시간 표시)
- **동그라미 도넛 게이지 인포그래픽 (`CircularQuotaCard`)**:
  - 스크린샷과 동일한 스타일의 원형 링 게이지 & 큼직한 퍼센트(%) 표시
  - 잔여량에 따른 직관적 컬러 변화 (초록 70%+ / 주황 30%+ / 빨강 30% 미만)
- **Gemini Models 전용 포커스**: 사용자가 주로 사용하는 Gemini 계열 주력 모델에 최적화

### 2. 콤팩트 플로팅 위젯 디자인
- **기본 모드 (초소형)**:
  - 주간 한도 및 5시간 한도 원형 인포그래픽 게이지만 깔끔하게 노출
- **고급 통계 토글 (▾ / ▴)**:
  - "고급 통계 보기" 클릭 시 오늘 누적 호출수, 프롬프트 수, 활성 세션 스텝 수, CLI 프로세스 RAM 사용량, 최근 세션 이력 표시
- **자유로운 창 이동 & 컨트롤**:
  - 위젯 본체 어디든 마우스 드래그로 손쉽게 위치 이동
  - **📌 창 항상 위에 고정 (Always-on-Top)** 토글
  - **◐ 투명도 조절** (`95%` ➔ `80%` ➔ `60%`)
  - **🔄 실시간 쿼터 즉시 새로고침** 버튼
  - **시스템 트레이 최소화 (✕)** 및 트레이 아이콘 메뉴 지원

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
├── AntiGravityMeter.csproj  # .NET 10 WinForms 프로젝트 설정 (System.Management 등)
├── Program.cs                # 메인 엔트리포인트 및 콘솔 테스트 모드
├── Models.cs                 # 쿼터 및 통계 메트릭 데이터 구조체 정의
├── CircularQuotaCard.cs      # 원형 도넛 링 게이지 인포그래픽 커스텀 컨트롤
├── AgyCollector.cs           # Language Server / SQLite / 프로세스 실시간 수집 엔진
├── WidgetForm.cs             # 미려한 다크 테마 커스텀 플로팅 위젯 UI
├── build.bat                 # 릴리즈 빌드 배치 스크립트
├── Run_Widget.bat            # 위젯 즉시 실행 배치 파일
├── README.md                 # 본 설명 문서
└── Publish\                  # 즉시 실행 가능한 단일 바이너리 배포 폴더
    ├── AntiGravityMeter.exe  # 실행 파일
    └── e_sqlite3.dll         # 경량 SQLite 엔진
```
