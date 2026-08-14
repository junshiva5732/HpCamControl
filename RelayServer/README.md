# HpCamControl Relay Server

FMETP STREAM 4의 WebSocket Room 서버(`Assets/FMETP_STREAM/FMWebSocket/TestServer_v4.0.0`)에서 꺼내온 코드입니다.
두 Android 기기가 인터넷을 통해 서로의 카메라 영상을 주고받으려면, 공인 IP를 가진 이 서버가 중계 역할을 해야 합니다.

## 1. VPS 준비

- 공인 IP가 있는 서버 아무거나 (AWS Lightsail, GCP, Vultr, Oracle Cloud Free Tier 등). 사양은 최소한도면 충분합니다 (1 vCPU / 1GB RAM).
- Node.js 18 LTS 이상 설치
- (권장) 도메인 하나를 서버 IP로 연결해두면 3번의 HTTPS 자동 설정이 훨씬 쉬워집니다.

## 2. 서버 코드 배포

이 `RelayServer` 폴더 전체를 VPS로 복사한 뒤:

```bash
npm install
npm start
```

`http://<서버IP>:3000/hello` 접속해서 "hello world"가 뜨면 정상 동작입니다.

## 3. Android에서 접속 가능하게 하기 (cleartext vs SSL)

**Android 9(API 28) 이상은 기본적으로 암호화 안 된 ws:// 접속을 차단합니다.** 아래 둘 중 하나를 선택하세요.

### 옵션 A — 빠른 테스트용 (도메인 없이, ws://)
개발 중 로컬 테스트에만 권장합니다. Unity의 Android 빌드에 `usesCleartextTraffic` 허용 설정(또는 network security config)을 추가해야 `ws://<IP>:3000`으로 접속됩니다. 이 부분은 Unity 에디터에서 Player Settings를 만질 때 같이 처리하겠습니다.

### 옵션 B — 실제 배포용 (도메인 필요, wss://, 추천)
[Caddy](https://caddyserver.com/) 리버스 프록시를 앞단에 두면 Let's Encrypt 인증서를 자동으로 발급/갱신해줘서 별도 인증서 관리 없이 `wss://` 를 바로 씁니다.

```bash
# Caddy 설치 (Ubuntu 예시)
sudo apt install -y debian-keyring debian-archive-keyring apt-transport-https
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | sudo tee /etc/apt/sources.list.d/caddy-stable.list
sudo apt update && sudo apt install caddy
```

`Caddyfile`의 `your-domain.com`을 실제 도메인으로 바꾸고 배치한 뒤 `sudo systemctl reload caddy`. 이후 Unity `FMWebSocketManager` 설정은:
- IP: `your-domain.com`
- Port: `443` (또는 `portRequired = false`)
- sslEnabled: `true`

## 4. 서버 상시 실행 (pm2)

VPS 재부팅/크래시에도 서버가 계속 떠 있도록 pm2로 관리합니다.

```bash
npm install -g pm2
pm2 start ecosystem.config.js
pm2 save
pm2 startup   # 출력되는 명령어를 그대로 한 번 더 실행하면 재부팅시 자동시작 등록됨
```

## 5. 방화벽 / 보안그룹

- 옵션 A: 3000번 포트를 인바운드로 열어야 함
- 옵션 B: 80(인증서 발급용), 443번 포트를 인바운드로 열어야 함

## 5.5 앱 종료 상태에서 깨우기 (FCM push-to-wake, 선택 기능)

앱이 완전히 종료된 상태에서도 상대방이 방에 들어오면 자동으로 깨워서 실행시키는 기능입니다. 이 기능을 쓰려면:

1. `RelayServer/secrets/firebase-adminsdk.json` 파일을 서버에 직접 배치해야 합니다 (Firebase 콘솔 → 프로젝트 설정 → 서비스 계정 → 새 비공개 키 생성으로 발급). **이 파일은 git에 안 올라갑니다** (`.gitignore`에 `/RelayServer/secrets/` 포함, Firebase 프로젝트 전체 권한을 가진 비밀키라서 절대 커밋하면 안 됨) — `scp`나 직접 업로드로 서버에 옮겨야 합니다.
2. `npm install` (firebase-admin 포함, 이미 `package.json`에 추가되어 있음)
3. 이 파일이 없어도 서버는 정상 동작합니다 — 그냥 push-to-wake 기능만 비활성화됩니다 (콘솔에 `-- Firebase Admin not initialized` 로그가 뜸).

## 6. 접속 테스트 (브라우저)

`public/index.html`은 FMETP STREAM 패키지에 포함된 간단한 웹 테스트 클라이언트입니다. `http://<서버>:3000` 또는 `https://your-domain.com`으로 접속해서 Room 접속이 되는지 먼저 브라우저로 확인해볼 수 있습니다.

## 다음 단계

서버가 뜨면, Unity 쪽에서 `Demo_FMWebSocketStreaming` 씬의 `FMWebSocketManager` IP/Port/SSL 설정을 여기서 배포한 서버 주소로 맞추고, 두 Android 기기가 같은 Room 이름으로 접속하도록 빌드합니다. (이 부분은 Unity 에디터 라이브 연결이 필요합니다.)
