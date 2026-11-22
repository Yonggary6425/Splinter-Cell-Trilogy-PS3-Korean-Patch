# sctkosub

동영상 자막 삽입 도구입니다. 패처가 영상마다 한 번씩 실행합니다.

```
sctkosub in.bik out.bik < frames
```

- 표준 입력: 프레임마다 "원본 프레임, 자막을 입힌 프레임"을 차례로 받습니다 (raw yuv420p).
- 두 프레임이 다른 블록만 고쳐 쓰고, 나머지 영상 데이터와 오디오는 원본을 그대로 복사합니다.
- 결과 파일은 원본과 같은 Bink 1 개정판(i)을 유지합니다.

빌드 (Windows용, zig 사용):

```
zig cc -O2 -target x86_64-windows-gnu -o sctkosub.exe sctkosub.c
```

`ref/binkdata.h`는 FFmpeg의 Bink 표 데이터입니다 (© Konstantin Shishkov, LGPL 2.1 이상).
