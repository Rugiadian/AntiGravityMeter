# ⚡ AntiGravityMeter (안티그라비티 CLI 사용량 데스크톱 위젯)

Google Antigravity CLI (`agy`)의 사용량, 호출 통계, 활성 세션 상태 및 리소스 점유율을 실시간으로 감시할 수 있는 **Windows 전용 초소형 플로팅 데스크톱 위젯**입니다.

---

## 🌟 주요 기능

### 1. 콤팩트 플로팅 위젯 디자인
- **화면 어디든 자유롭게 드래그**: 타이틀바뿐만 아니라 위젯 영역 어디를 클릭하고 끌어도 부드럽게 이동합니다.
- **2단계 뷰 모드 토글 (▾ / ▴)**:
  - **컴팩트 모드 (기본)**: 너비 340px, 높이 158px로 화면 공간을 최소한으로 차지하면서 핵심 수치만 노출
  - **상세 모드 (펼침)**: 최근 대화 세션 목록(제목, 스텝 수, 최종 수정 시각) 및 최근 보낸 프롬프트 이력 확인
- **📌 창 항상 위에 고정 (Always-on-Top)**: 작업 중 다른 창 뒤로 숨지 않도록 상시 고정 토글 지원
- **◐ 투명도 조절**: 클릭할 때마다 `95%` ➔ `80%` ➔ `60%`로 투명도가 순환 변경되어 배경 작업 시 방해되지 않습니다.
- **시스템 트레이 지원 (✕)**: 닫기 클릭 시 시스템 트레이로 최소화되며, 더블 클릭 시 다시 나타납니다.

### 2. 실시간 지표 & 감시 항목
- **⚡ 실시간 CLI 프로세스 상태 (Live Pulse)**:
  - 🟢 **BUSY (생각/도구 실행 중)**: 초록색 펄스 애니메이션 게이지 점등
  - 🟡 **IDLE (대기 중)**: 프로세스 대기 상태
  - ⚪ **OFF (종료됨)**: CLI 비활성 상태
- **🔥 오늘 API 생성 호출 수**: 오늘 하루 동안 LLM 추론(`streamGenerateContent`)이 실행된 누적 횟수
- **💬 오늘 보낸 프롬프트 수**: 오늘 사용자가 CLI에 전송한 질문/명령어 총 횟수
- **🎯 활성 세션 스텝 수**: 현재 진행 중인 대화 세션의 누적 스텝(turn) 수
- **⚡ CLI 프로세스 RAM 사용량**: `agy.exe`의 실제 작업 메모리 점유율(MB)
- **🤖 활성 모델명**: 현재 사용 중인 주력 모델 (`gemini-3.8-flash`, `gemini-2.5-pro` 등)
- **🕒 마지막 활동 시각**: 최근 명령 입력 또는 응답이 발생한 시간 (초/분 단위)

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
├── Program.cs                # 메인 엔트리포인트 및 고해상도 DPI 초기화
├── Models.cs                 # 메트릭 데이터 구조체 정의
├── AgyCollector.cs           # SQLite / 로그 / 프로세스 실시간 데이터 수집 엔진
├── WidgetForm.cs             # 미려한 다크 테마 커스텀 플로팅 위젯 UI
├── build.bat                 # 릴리즈 빌드 배치 스크립트
├── Run_Widget.bat            # 위젯 즉시 실행 배치 파일
├── README.md                 # 본 설명 문서
└── Publish\                  # 즉시 실행 가능한 바이너리 배포 폴더
    ├── AntiGravityMeter.exe  # 실행 파일 (484KB)
    └── e_sqlite3.dll         # 경량 SQLite 엔진
```
