# Общий каталог DNS Dagami

Dagami Agent Proxy для Windows загружает каталог с `https://dagami.ru/dns-catalogue.json`. Каталог доступен в разделе **DNS и сброс → Каталог DNS**. Здесь можно обновить список и выбрать провайдера. «Применить для приложений» сохраняет выбор. Подбор подключения для Gemini и Antigravity также может проверить провайдеров из каталога.

## Редактирование

Список провайдеров хранится на сервере в `/etc/dagami-dns-catalogue/providers.json`. Добавьте или удалите запись в `Providers`.

- `Id` — постоянный идентификатор, например `example-dns`.
- `Name` — название в приложении.
- `Url` — официальный DoH с путём, например `https://dns.dagami.ru/dns-query`, или DoT, например `tls://dns.dagami.ru:853`.
- `Addresses` — дополнительные IPv4, если провайдер публикует их отдельно. Оставьте `[]`, чтобы искать адреса по имени.

Для проверки и немедленного обновления выполните две команды на хосте каталога.

```sh
python3 /opt/dagami-dns-catalogue/refresh.py /etc/dagami-dns-catalogue/providers.json /var/lib/dagami-dns-catalogue/catalogue.json --check
systemctl start dagami-dns-catalogue.service
```

Таймер обновляет адреса каждые 15 минут. Сервер разрешает имена из файла, проверяет TLS с именем провайдера и отправляет DNS-запрос. Если все проверки провайдера временно упали, копия с предыдущей проверки сохраняется до суток для адресов, которые всё ещё объявлены. Удаление записи применяется при ближайшем успешном запуске. Приложение перепроверяет IPv4 из своей сети и сохраняет последнюю копию. Проверки DNS не подтверждают вход в аккаунт или ответ модели.

## Установка

Установите `refresh.py` в `/opt/dagami-dns-catalogue/`, `providers.json` в `/etc/dagami-dns-catalogue/`, а оба юнита в `/etc/systemd/system/`. После `systemctl daemon-reload` запустите сервис один раз, проверьте результат, затем включите таймер командой `systemctl enable --now dagami-dns-catalogue.timer`.

Если сайт уже обслуживает статические файлы из `/var/www/dagami/current`,
достаточно добавить ссылку. При замене каталога сайта перенесите эту ссылку.

```sh
ln -s /var/lib/dagami-dns-catalogue/catalogue.json /var/www/dagami/current/dns-catalogue.json
```

Если статические ссылки недоступны, добавьте маршрут в веб-конфигурацию.

```nginx
location = /dns-catalogue.json {
    alias /var/lib/dagami-dns-catalogue/catalogue.json;
    default_type application/json;
    add_header Cache-Control "public, max-age=300";
}
```

При изменении веб-конфигурации сделайте резервную копию и проверьте её перед перезагрузкой. Запросите JSON снаружи. Внешний HTTP-доступ нужен только для чтения готового файла. Для отката уберите ссылку или статический маршрут и выключите таймер.
