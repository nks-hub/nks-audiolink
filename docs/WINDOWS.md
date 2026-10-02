# Windows klient

Grafická aplikace používá Windows 10 nebo novější. Vydání pro `win-x64` obsahuje .NET runtime; pro sestavení ze zdrojů je potřeba .NET SDK 9:

```powershell
dotnet run --project src/NksAudioLink.App
```

V aplikaci zadejte adresu serveru nebo použijte **Najít**, vyberte zdroj, nastavte hlasitost a cílový buffer a stiskněte **Připojit**. Zesílení lze měnit během přenosu. Ostatní nastavení se mění po odpojení. Panel stavu ukazuje odhad latence, buffer serveru, zpoždění výstupu, RTT a počty ztrát, pozdních rámců, podtečení a přetečení.

Zavření okna aplikaci ponechá v oznamovací oblasti. Nabídka ikony umožňuje připojení, odpojení, otevření okna a úplné ukončení. Volba **Spouštět s Windows** přidá záznam pouze pro aktuálního uživatele. Nastavení se ukládá do `%AppData%\NksAudioLink\settings.json`; sdílený klíč se do tohoto souboru neukládá.

## Režimy

- **Celý zvuk PC:** WASAPI loopback vybraného výstupního zařízení. Zvuk je současně dostupný i na místním výstupu.
- **Virtuální zvukovka:** zachytává CABLE Output a po dobu spojení nastaví výchozí výstup Windows na CABLE Input. Vyžaduje samostatně nainstalovaný VB-CABLE.
- **Jiný vstup:** zachytává vybrané záznamové zařízení. Výchozí výstup Windows se nemění.

Windows aplikace mohou mít vlastní pevně zvolený výstup. Pro virtuální režim v nich vyberte systémový výchozí výstup nebo CABLE Input.

## Příprava VB-CABLE

VB-CABLE je samostatný ovladač výrobce VB-Audio. Z jeho [oficiální stránky](https://vb-audio.com/Cable/) stáhněte Pack45, rozbalte celý archiv a spusťte `VBCABLE_Setup_x64.exe` jako správce. Před spuštěním lze ověřit podpis:

```powershell
Get-AuthenticodeSignature .\VBCABLE_Setup_x64.exe |
    Format-List Status, SignerCertificate
```

Podpis má být platný a podepisovatel BUREL VINCENT. Potvrďte instalaci v samostatném instalátoru a dokončete ji restartem PC podle návodu výrobce. Potom v AudioLink použijte **Obnovit**; musí se objevit aktivní CABLE Input a CABLE Output.

Ovladač není součástí vydání AudioLink. Jeho přiložená licence vyžaduje souhlas autora pro integraci do instalačního procesu jiného programu, proto aplikace nabízí odkaz na výrobce a detekci již nainstalovaného zařízení. Podmínky a donationware model popisuje [VB-Audio](https://vb-audio.com/Services/licensing.htm).

Před změnou výchozího zařízení AudioLink uloží původní výstupy všech tří rolí Windows. Při odpojení a ukončení je obnoví. Po násilném ukončení provede obnovu při dalším spuštění. Ručně změněný výstup během přenosu nepřepisuje. Pokud obnova selže kvůli nedostupnému původnímu zařízení, aplikace zobrazí chybu a zachová zálohu v `%AppData%\NksAudioLink\default-playback-backup.json`.

## CLI

```powershell
dotnet run --project src/NksAudioLink.Cli -- devices
dotnet run --project src/NksAudioLink.Cli -- discover
dotnet run --project src/NksAudioLink.Cli -- send --server SERVER_IP --seconds 15 --gain 0.5
dotnet run --project src/NksAudioLink.Cli -- test-tone --server SERVER_IP --seconds 10 --gain 0.01
```

`send` podporuje `--mode loopback|capture`, `--device ID`, `--latency MS`, `--port`, `--config FILE` a `--takeover true` pro vědomé převzetí obsazeného serveru. Bez `--seconds` běží do Ctrl+C. `--gain` používá desetinnou tečku a rozsah 0 až 4. CLI v režimu capture samo nepřepíná výchozí zvukovku; automatické směrování zajišťuje grafická aplikace.
