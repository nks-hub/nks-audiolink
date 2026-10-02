# NKS AudioLink

Ve vývoji: Windows klient posílá zvuk přes LAN do serveru, který jej přehrává na fyzické zvukové kartě.

```text
Windows audio → WASAPI capture → UDP PCM → jitter buffer → Linux ALSA → fyzický výstup
```

Plánovaný režim virtuální zvukovky používá samostatně instalovaný, podepsaný ovladač VB-CABLE. Alternativní režim zachytává celý výstup Windows přes WASAPI loopback. Výchozí formát přenosu je stereo PCM s16le, 48 kHz, rámce 5 ms. Cílem je latence pod 100 ms; dosud není změřená.

## Stav

Fáze 0 a 1 jsou hotové: řešení se sestaví na Windows i Linuxu; Core obsahuje UDP protokol, HMAC, jitter buffer, resampler a konfiguraci. Přenos zvuku, GUI ani nasazení zatím nejsou dokončené. Průběh a kritéria ověření jsou v [TODO.md](TODO.md). Žádné binárky ovladače ani konfigurace konkrétní instalace nejsou součástí repozitáře.

Lokálně prošlo 27 testů a pokrytí řádků Core je 85,42 % (`dotnet test tests/NksAudioLink.Tests --collect:"XPlat Code Coverage"`).

## Sestavení

Vyžaduje .NET SDK 9. Na Windows: `dotnet build NksAudioLink.sln` a `dotnet test NksAudioLink.sln`.

## Licence

Zatím nebyla zvolena veřejná licence. Všechna práva vyhrazena.
