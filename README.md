# 스플린터 셀 트릴로지 한글패치 (PS3)

**Tom Clancy's Splinter Cell Trilogy** (PS3, **BLUS30761**, USA/Korea) 한글패치입니다.
1편 『스플린터 셀』, 2편 『판도라 투모로우』, 3편 『혼돈 이론』을 한글로 즐길 수 있습니다.

> 본 한글패치는 개인이 제작한 비공식·비상업적 팬 한글패치입니다. — by **Yonggary6425 (용가리)**
> 게임 파일(ISO)은 포함되어 있지 않습니다. 직접 소유한 원본으로만 사용해 주세요.

---

## 주요 특징

- **1·2·3편 영어 텍스트 한글화**: 메뉴, 임무 목표, 데이터·노트, 대화 자막, 로딩 화면
- **게임 중 음성 자막**: 원본에 자막이 없던 대사에도 한글 자막을 넣었습니다. 1편의 긴 통화 장면은 음성에 맞춰 나눠서 나옵니다.
- **컷신 동영상 자막 (선택)**: 오프닝을 포함한 동영상 52개에 한글 자막을 입힐 수 있습니다. 필요한 도구(ffmpeg)는 패처가 자동으로 받고, 끝나면 지웁니다.
- **ISO와 게임 폴더 모두 설치**: ISO는 원본을 두고 `원본이름_KOR.iso`를 새로 만들고, 폴더는 원본을 `.orig`로 보관합니다.
- **되돌리기 지원**: 폴더 설치는 버튼 하나로 원본으로 돌아갑니다.
- **RPCS3 설치 데이터 함께 패치**: 이미 게임을 실행해 `dev_hdd0`에 설치된 데이터도 같이 바꿉니다.
- 컬렉션 선택 화면과 각 편 타이틀 화면에 `LOCALIZATION BY YONGGARY` 표기

## 준비물

- **Tom Clancy's Splinter Cell Trilogy (USA, Korea)** / **BLUS30761** 원본 ISO 또는 게임 폴더
- Windows 10/11 (.NET Framework 4.x, 기본 설치되어 있음)
- RPCS3 (PC에서 플레이할 때)

## 패처 2종

[Releases](../../releases)에서 최신 `SCT_KO_Patcher_v1.1.zip`을 받아 압축을 풉니다. 둘 중 하나만 쓰면 됩니다.

| 파일 | 대상 | 설명 |
|---|---|---|
| `SCT_KO_Patcher_RPCS3.exe` | RPCS3 (권장) | 실행 파일은 원본 그대로 두고, RPCS3 게임 패치(`patches\BLUS30761_patch.yml`)로 한글을 표시합니다. 복호화가 필요 없습니다. RPCS3 폴더 지정이 필수입니다. |
| `SCT_KO_Patcher_ELF.exe` | RPCS3, PS3 실기 | 실행 파일 자체를 패치합니다. RPCS3 **Utilities → Decrypt PS3 Binaries**로 `.self` 3개를 한 번 복호화해야 합니다. |

게임 데이터, ISO/폴더 설치, 동영상 자막, 되돌리기는 두 패처가 같습니다.

## 적용 방법 (RPCS3 권장 방식)

1. `SCT_KO_Patcher_RPCS3.exe`를 실행합니다.
2. **게임 폴더 / ISO**에 원본 ISO 또는 게임 폴더(`PS3_GAME`이 들어 있는 폴더)를 지정합니다.
3. **RPCS3 폴더**에 `rpcs3.exe`가 있는 폴더를 지정합니다.
4. 원하면 **동영상에 한글 자막 입히기**를 켭니다. (영상 하나에 수 초~1분)
5. **[설치]** 버튼을 누릅니다.
6. RPCS3 **Manage → Game Patches**에서 "Korean patch (SC1/SC2/SC3)"가 켜져 있는지 확인하고 게임을 실행합니다.

### 실행 파일 패치 방식 (`SCT_KO_Patcher_ELF.exe`)

1. ISO 설치라면 **[ISO에서 실행 파일 꺼내기]** 버튼을 누릅니다. ISO 옆 `이름_SELF` 폴더에 `sc1/sc2/sc3.ppu.self`가 생깁니다.
   폴더 설치라면 게임 폴더의 `Data_SC\sc1.ppu.self`, `Data_SCPT\sc2.ppu.self`, `Data_SCCT\sc3.ppu.self`를 그대로 씁니다.
2. RPCS3 **Utilities → Decrypt PS3 Binaries**에서 위 `.self` 3개를 선택합니다. 같은 폴더에 `.elf`가 만들어집니다.
3. 패처에서 **[설치]** 버튼을 누릅니다.

### PS3 실기

- "PS3 실기"를 선택하면 패치된 ELF를 `SCT_KO_ELF` 폴더에 저장합니다.
- 이 ELF를 재서명 도구(TrueAncestor SELF Resigner, scetool 등)로 SELF로 만들어 게임의 `sc1/sc2/sc3.ppu.self`와 바꿔 주세요.
- PS3에 이미 설치된 게임 데이터(BLUS30761DATA)는 지운 뒤 실행하세요.
- **PS3 실기에서는 아직 테스트하지 못했습니다.**

## 주의

- 항상 **원본**에 적용해 주세요. 이전 패치본 ISO에 다시 적용하지 마세요.
- RPCS3로 이미 게임을 실행한 적이 있다면 RPCS3 폴더를 꼭 지정하세요. 게임은 `dev_hdd0`에 설치된 데이터를 읽기 때문에, 지정하지 않으면 한글이 나오지 않습니다.
- 테스트 환경: RPCS3 v0.0.43, v0.0.42-19743

## 알려진 사항

- 1편의 일부 대사(CIA 분석관 테이프 장면, 대통령궁 일부)는 자막이 없습니다.
- 3편의 실시간 컷신 일부는 자막이 없습니다.
- 동영상 자막을 입히지 않으면 컷신 영상은 영어 음성만 나옵니다.

## 구성

```
release/          배포 파일 (SCT_KO_Patcher_v1.1.zip)
patcher-src/      패처 소스 (C#, PowerShell 빌드 스크립트)
credit/           타이틀 화면 크레딧 문구 도구
README.md / RELEASE_NOTES.md / NOTICE.md
```

패처에는 원본과 달라진 부분(차이 데이터)과 한글 데이터만 들어 있습니다. 차이 데이터는 원본 게임이 있어야 만들 수 있으므로, 소스만으로는 배포용 exe를 다시 만들 수 없습니다.

## 크레딧

- 한글화·패처 제작: **Yonggary6425 (용가리)**
- 동영상 자막 글꼴: 나눔고딕 (NAVER, SIL Open Font License 1.1)
- 원작: Tom Clancy's Splinter Cell Trilogy © Ubisoft Entertainment. 모든 권리는 원저작권자에게 있습니다.

## 버그 제보

오역·오탈자·표시 문제는 [Issues](../../issues)에 몇 편인지, 임무·장면과 스크린샷을 함께 올려 주세요.
