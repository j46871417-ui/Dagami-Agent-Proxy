# Подготовка Android

Прототип 0.1 содержит экран выбора маршрута, привязку ключа из бота и проверку HTTPS для прямого подключения, своего прокси и Dagami. Клиент использует общий Go-транспорт. Режимы проверяют сеть самого клиента. Для маршрутизации других приложений нужен следующий этап с VpnService.

Ключ устройства хранится в Android Keystore. Код из `/key` очищается из формы после отправки и не сохраняется. Адрес своего прокси вводится на текущий запуск.

Для своего адреса доступны HTTP, HTTPS, SOCKS4/4a, SOCKS5 и `socks5s://`
(SOCKS5 поверх TLS). Для Dagami можно отдельно включить диагностику HTTP/2
CONNECT на шлюзе, запущенном с `-http2`.

## Сборка прототипа

Используйте распакованный исходный ZIP. Для Android нужны Go 1.26+, JDK 17, Gradle 8.9, Android SDK Platform 35 и Android NDK. Укажите `ANDROID_HOME` и добавьте каталог `go env GOBIN` (или `GOPATH/bin`, если GOBIN пуст) в PATH. Android Gradle Plugin 8.7.3 закреплён в проекте. [Совместимость AGP](https://developer.android.com/build/releases/agp-8-7-0-release-notes)

Команды из корня исходников для macOS/Linux:

```sh
go get -tool golang.org/x/mobile/cmd/gobind@v0.0.0-20261011034528-197770444867
go install golang.org/x/mobile/cmd/gomobile@v0.0.0-20261011034528-197770444867
go install golang.org/x/mobile/cmd/gobind@v0.0.0-20261011034528-197770444867
mkdir -p android/app/libs
gomobile bind -target=android/arm64,android/amd64 -androidapi=26 -javapkg=ru.dagami.binding -o android/app/libs/dagami-mobile.aar ./mobile
gradle -p android :app:assembleDebug
```

Готовый отладочный APK появится в `android/app/build/outputs/apk/debug/`. AAR включает ARM64 для телефона и x86_64 для эмулятора. [Документация gomobile](https://pkg.go.dev/golang.org/x/mobile/cmd/gomobile)

## Уже отделено в Go

- `internal/managed`: протокол устройства, доступ к прокси и TLS-транспорт.
- `ProofSigner` и `SignUsing`: подпись без экспорта приватного ключа.
- `RedeemUsing`: получение доступа с платформенным signer.
- `SignatureFromDER`: перевод Android ECDSA DER в используемый шлюзом `r || s`.
- Проверка собственного результата подписи до отправки и тест совместимости
  с серверным проверяющим кодом. Изменение серверного протокола не требуется.
- TLS повторяется целиком на разрешённых зарубежных входах до отправки CONNECT,
  с исходным SNI и проверкой сертификата. Пользовательские шлюзы не перенаправляются.

Адаптер Java хранит P-256 ключ в Android Keystore и реализует SHA256withECDSA
над **исходным сообщением**, затем передаёт DER через `SignatureFromDER`.
Нельзя повторно хешировать уже посчитанный SHA-256. Приватный ключ остаётся в Keystore.

## Следующие этапы

1. Собрать и проверить прототип на устройстве с Android Keystore.
2. `VpnService`: явное системное согласие, foreground service, per-app allowlist,
   `protect()` каждого исходящего сокета, чтобы не создать петлю VPN.
3. TUN → TCP-транспорт и перехват DNS. Обычный HTTP-proxy в `VpnService.Builder`
   является рекомендацией приложениям и не заменяет TUN. PAC там не поддерживается.
4. DoH/DoT через выбранный маршрут, ограниченные кеши, отмена при смене сети,
   повторное подключение с backoff/jitter. Не пытаться менять глобальный Private DNS
   обычным приложением; эта возможность предназначена для владельца устройства.
5. Сначала TCP для выбранных приложений. Текущий шлюз не предоставляет произвольный
   UDP/QUIC: это отдельная работа, а не скрытое обещание совместимости.
6. Изучить Intra: обход сокетов VPN, обработку DNS, split-retry для TCP/443.
   Его Apache-2.0 не отменяет проверку лицензий зависимостей. Split-retry не поможет
   при полной блокировке IP и не гарантирует обход любого сетевого фильтра.
7. Проверки на устройстве: Wi-Fi ↔ LTE, sleep/doze, отзыв разрешения, другой VPN,
   captive portal, IPv6, остановка процесса и восстановление после обновления.

Источники: [Android VPN](https://developer.android.com/develop/connectivity/vpn),
[VpnService.Builder](https://developer.android.com/reference/android/net/VpnService.Builder),
[Android Keystore](https://developer.android.com/privacy-and-security/keystore),
[DevicePolicyManager](https://developer.android.com/reference/android/app/admin/DevicePolicyManager),
[Intra](https://github.com/Jigsaw-Code/Intra).
