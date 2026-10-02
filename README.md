# NKS AudioLink

Ve vývoji: Windows klient posílá zvuk přes LAN do serveru, který jej přehrává na fyzické zvukové kartě.

```text
Windows audio → WASAPI capture → UDP PCM → jitter buffer → Linux ALSA → fyzický výstup
```

Plánovaný režim virtuální zvukovky používá samostatně instalovaný, podepsaný ovladač VB-CABLE. Alternativní režim zachytává celý výstup Windows přes WASAPI loopback. Výchozí formát přenosu je stereo PCM s16le, 48 kHz, rámce 5 ms. Cílem je latence pod 100 ms; dosud není změřená.

## Stav

Fáze 0–2 jsou hotové: řešení se sestaví na Windows i Linuxu; Core obsahuje UDP protokol, HMAC, jitter buffer, resampler a konfiguraci. UDP server umí přijmout 5ms PCM rámce a zapsat je do WAV nebo do nulového sinku. ALSA výstup, zachytávání zvuku z Windows a GUI zatím nejsou dokončené. Průběh a kritéria ověření jsou v [TODO.md](TODO.md). Žádné binárky ovladače ani konfigurace konkrétní instalace nejsou součástí repozitáře.

Lokálně prošlo 27 testů a pokrytí řádků Core je 85,42 % (`dotnet test tests/NksAudioLink.Tests --collect:"XPlat Code Coverage"`).

## Ověření přenosu do WAV

Na Linuxu sestavte self-contained server pomocí `dotnet publish src/NksAudioLink.Server -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true`. Pro příjem z jiného počítače nastavte v soukromém souboru `server.json` `allowCidrs` na odpovídající lokální subnet; výchozí konfigurace povoluje jen loopback.

```sh
./NksAudioLink.Server run --config server.json --sink wav:/tmp/tone.wav
```

Na Windows odešlete testovací tón:

```powershell
dotnet run --project src/NksAudioLink.Cli -- test-tone --server SERVER_IP --seconds 5
py -3 tests/verify_tone.py tone.wav
```

CLI používá `--port` (výchozí 7355), `--seconds` a `--config`. Konfigurační soubor klienta podporuje `server`, `port`, `targetLatencyMs`, `gain` a volitelný `pskBase64`; server podporuje `port`, `name`, `sink`, `allowCidrs`, `pskBase64`, `idleReleaseSec` a `targetLatencyMs`. Klíč PSK musí obsahovat alespoň 16 náhodných bajtů zakódovaných Base64 a konfigurační soubory s klíči nepatří do Gitu. Přenos není šifrovaný; PSK ověřuje původ paketů.

Protokol je rozepsaný v [docs/PROTOCOL.md](docs/PROTOCOL.md).

## Ověřený výsledek

2026-10-02, Windows → Linux x86_64 přes LAN, WAV sink: 5,095 s, RMS 8 484, dominantní energie na 440 Hz, 0 přetečení, 0 pozdních rámců. Zatím nejde o měření latence ani o poslechový test fyzického výstupu.

## Sestavení

Vyžaduje .NET SDK 9. Na Windows: `dotnet build NksAudioLink.sln` a `dotnet test NksAudioLink.sln`.

## Licence

Zatím nebyla zvolena veřejná licence. Všechna práva vyhrazena.
