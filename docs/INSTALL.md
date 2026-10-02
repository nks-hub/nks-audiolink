# Instalace serveru na Linuxu

Server je konzolová aplikace v C#/.NET 9. Na Linuxu zapisuje přímo do ALSA přes `libasound.so.2`; PulseAudio ani PipeWire nepotřebuje. Postup cílí na Debian/Proxmox hostitele x86_64 s fyzickou zvukovou kartou dostupnou přes `/dev/snd`.

## Předpoklady

- Na sestavovacím počítači je .NET SDK 9. Cílový počítač potřebuje `libasound.so.2`, systemd, skupinu `audio` a funkční ALSA zařízení. Při publikaci `--self-contained` nepotřebuje .NET runtime.
- Cílový stroj přijímá UDP na zvoleném portu, výchozí je 7355. Povolte jej jen z důvěryhodné LAN nebo VPN. PCM data nejsou šifrovaná.
- Před změnami ověřte, že vybraná zvuková karta není vyhrazena jiné aplikaci. Instalátor zařízení neotevírá a službu sám nespouští ani nerestartuje.

## Sestavení

V kořeni repozitáře:

```sh
dotnet publish src/NksAudioLink.Server/NksAudioLink.Server.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -o publish/linux-x64
```

Na cílový stroj přeneste `publish/linux-x64/NksAudioLink.Server` a adresář `deploy/linux`. Následující příkazy na cíli spusťte v root shellu (na strojích se `sudo` lze použít `sudo sh ...`). Instalátor nastaví oprávnění cílové binárky:

```sh
sh deploy/linux/install.sh ./NksAudioLink.Server
```

Instalátor vytvoří neprivilegovaného uživatele `nks-audiolink` ve skupině `audio`, uloží binárku do `/opt/nks-audiolink`, jednotku systemd do `/etc/systemd/system` a při první instalaci vzorovou konfiguraci do `/etc/nks-audiolink/server.json`. Opakované spuštění zachová obsah konfigurace a nastaví její práva na `root:nks-audiolink 0640`; symbolické odkazy odmítne. Binárku vymění atomicky, takže lze soubory aktualizovat i za běhu služby; nová verze začne pracovat po plánovaném restartu. Instalátor zavolá pouze `systemctl daemon-reload`.

## Konfigurace a spuštění

Upravte `/etc/nks-audiolink/server.json` před prvním spuštěním. Vzor povoluje jen `127.0.0.0/8`; nastavte `allowCidrs` na skutečný rozsah důvěryhodných klientů. Nepoužívejte bez rozmyslu `0.0.0.0/0`. Pokud chcete ověřování paketů, nastavte stejný `pskBase64` na serveru i klientovi; klíč musí mít alespoň 16 náhodných bajtů. Konfiguraci s klíčem neukládejte do Gitu a ponechte přístup pouze rootovi a skupině služby.

Hodnota `sink` je `alsa:default` nebo například `alsa:plughw:CARD=Device,DEV=0`. Konkrétní ALSA identifikátor zjistíte z `/proc/asound/cards` nebo z výstupu `NksAudioLink.Server devices`. `plughw` dovolí ALSA převést formát, pokud karta přímo nepřijímá stereo S16_LE při 48 kHz. Karta musí být přístupná uživateli ve skupině `audio`. U připojitelné USB karty preferujte stabilní identifikátor ALSA před číslem karty, které se po restartu může změnit.

```sh
systemctl enable --now nks-audiolink
systemctl status nks-audiolink
journalctl -u nks-audiolink -f
```

Při aktualizaci spusťte instalátor znovu a službu restartujte v dohodnutém čase:

```sh
systemctl restart nks-audiolink
```

Server otevírá ALSA až při přijetí relace. Po ukončení relace zařízení zavře. Pro krátkou izolovanou zkoušku bez zvukové karty lze server spustit ručně s `--sink null` nebo `--sink wav:/tmp/tone.wav`; WAV soubor musí být zapisovatelný uživatelem, pod nímž proces běží.

## Ověření

```sh
/opt/nks-audiolink/NksAudioLink.Server devices
getent group audio
id nks-audiolink
```

Na Windows odešlete testovací tón pomocí klientského CLI (adresu a případný klíč nastavte pro vlastní síť):

```powershell
dotnet run --project src/NksAudioLink.Cli -- test-tone --server SERVER_IP --seconds 10
```

CLI vypisuje statistiky bufferu, podtečení, přetečení a ztracených paketů. Pro ověření na fyzickém zařízení přepněte zesilovač na vstup, do kterého je zvuková karta zapojena, a poslechněte tón. Dlouhodobý test a měření latence jsou samostatné kroky; samotné úspěšné spuštění služby je neprokazuje.

Při potížích zkontrolujte `journalctl -u nks-audiolink`, přístup ke `/dev/snd`, správnou hodnotu `sink`, `allowCidrs` a příchozí UDP port. Chyba `ALSA open` často znamená neexistující nebo obsazené zařízení. Služba nepotřebuje PulseAudio, PipeWire ani desktopové sezení.
