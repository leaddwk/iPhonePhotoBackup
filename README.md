# iPhone Photo Backup for Windows

USB로 연결한 iPhone의 사진과 동영상을 Windows PC의 폴더나 외장하드에 복사하는 도구입니다. Windows에 공개된 로컬 사진 보관함을 읽으며 iCloud 로그인 없이 사용할 수 있습니다.

## 다운로드 및 사용

1. [GitHub Releases](../../releases)에서 `iPhonePhotoBackup.exe`를 다운로드합니다.
2. iPhone의 잠금을 풀고 USB로 연결한 뒤 **이 컴퓨터를 신뢰하겠습니까?**가 표시되면 **신뢰**를 누릅니다.
3. 아이폰의 **설정 → 앱 → 사진 → 원본 유지**를 선택하면 USB 전송 중 HEIF와 HEVC의 자동 변환을 피할 수 있습니다. [Apple 원본 유지 안내](https://support.apple.com/ko-kr/116944)
4. 프로그램에서 외장하드 폴더를 선택합니다. **전체 백업**은 USB로 공개된 파일을 대상으로 하고, **갤러리 열기**에서는 원하는 항목을 고를 수 있습니다.
5. 중단했거나 실패한 항목이 있으면 같은 폴더에서 **남은 파일만 백업**을 실행합니다. 끝난 뒤 **백업 기록 열기**에서 실패와 남은 파일 수를 확인합니다.

Windows 10/11과 .NET Framework가 필요합니다. 파일 탐색기에 iPhone이 나타나지 않으면 Apple 기기 앱과 USB 연결 상태를 확인하세요. [Apple의 Windows 전송 안내](https://support.apple.com/ko-kr/120267)

## 백업 범위 및 검증 방식

- 사진, 동영상과 함께 USB에 표시되는 Live Photo의 MOV 및 AAE 편집 파일을 복사합니다. 가려져 있거나 Windows에 공개되지 않는 항목, 파일 앱의 다운로드 폴더, 사진 앱의 앨범 구조는 백업에 포함되지 않을 수 있습니다.
- 갤러리는 날짜, 파일 종류, 이름으로 검색할 수 있습니다. 즐겨찾기나 저장 앱은 아이폰에서 해당 메타데이터를 읽을 수 있을 때 표시합니다.
- 새로 저장한 파일은 USB로 받은 바이트의 SHA-256과 외장하드에 기록된 파일의 SHA-256을 비교합니다. 이 비교는 파일의 재생 가능성이나 아이폰 사진 앱 전체의 백업 여부를 확인하지 않습니다.
- **남은 파일만 백업**은 완료 기록과 기존 파일의 크기 및 확인 가능한 수정 시각을 대조합니다. 크기와 수정 시각이 같은 손상은 이 모드에서 발견되지 않을 수 있습니다. **전체 백업**은 완료 기록이 있는 기존 파일의 SHA-256을 다시 검사합니다.

## 면책 및 사용 책임

이 프로그램은 모든 iPhone 모델, iOS 및 Windows 버전, USB 연결 및 저장장치 조합에서 검증되지 않았습니다. 소스는 아이폰의 사진과 동영상에 읽기 전용으로 접근하도록 작성되어 있으나, 모든 환경에서 아이폰 내 파일이 수정·삭제·손상되지 않는다는 보장은 없습니다. 백업이 완전하거나 복사된 모든 파일이 정상적으로 열리거나 복원된다는 보장도 없습니다.

프로그램은 **있는 그대로** 제공되며, 사용과 백업 결과 확인은 사용자 책임입니다. 관련 법률이 허용하는 범위에서 개발자와 기여자는 프로그램의 사용 또는 사용 불능으로 발생한 데이터 손실, 파일 손상 및 기타 피해에 대해 책임을 지지 않습니다. 아이폰의 파일을 지우기 전에 백업 기록을 확인하고 중요한 사진과 동영상이 실제로 열리는지 확인하세요. 중요한 데이터는 별도 사본도 보관하세요.

## 소스에서 빌드

Windows 명령 프롬프트에서 다음 명령을 실행합니다.

```bat
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /codepage:65001 /target:winexe /out:iPhonePhotoBackup.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll /reference:System.Xaml.dll /reference:WPF\PresentationCore.dll /reference:WPF\WindowsBase.dll src\*.cs
```
