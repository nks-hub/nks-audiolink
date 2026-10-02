# Ověření

Stav k 2026-10-02. Lokální konfigurace a nezkrácené logy zůstávají mimo Git.

| Oblast | Výsledek |
|---|---|
| Přenositelný Core/server a fronta klienta | 39 automatických testů prošlo; protokol, autentizace, jitter, drift, sinky a takeover |
| Windows klient | 6 integračních testů přes UDP loopback prošlo: tichý tok, pozdní start, restart serveru, obnova zdroje po chybě capture a úplné zastavení po odmítnutí busy/unsupported |
| Windows → Linux WAV | Tón 440 Hz, délka 5,095 s, RMS 8 484; nulová přetečení a pozdní rámce |
| Nasazený ALSA/systemd server | Nejnovější souvislý 600s přenos: nula podtečení, přetečení, ztrát a pozdních rámců v klientských i závěrečných serverových statistikách |
| Uvolnění zařízení | Po odpojení klienta služba uvolnila ALSA zařízení po nastaveném intervalu |
| Skutečný WASAPI klient | Zachycený zvuk potvrzen ve WAV; přenos na LAN se obnovil po restartu systemd služby |
| Živé UI | Připojení, odpojení, discovery, statistiky a změna gain během přenosu ověřeny |
| Virtuální režim | Skutečný 440Hz signál CABLE Input → CABLE Output → UDP WAV potvrzen analýzou souboru |
| Obnova výchozího zařízení | Všechny tři role Windows obnoveny po odpojení i dalším startu po násilném ukončení aplikace |
| Živý výstup PC | Nenulový signál na vybraném render endpointu, klient připojený a fyzický ALSA výstup ve stavu RUNNING |
| Publikované balíčky | Windows CLI se spustilo z balíčku; Linux singlefile server spustil `--help` na cílovém hostiteli bez instalace .NET |

VB-CABLE byl nainstalován samostatným podepsaným instalátorem po souhlasu majitele. Testovací PC nebyl restartován. Ovladač ani instalátor nejsou součástí projektu.

## Latence a CPU

UI při živém přenosu ukazovalo odhad 74 ms. Součet zahrnuje skutečnou délku capture bufferu, cílovou frontu klienta, rámcování, polovinu RTT a aktuální buffer a delay serveru. Nezahrnuje neznámé zpoždění za fyzickou zvukovou kartou. Fyzické měření a posouzení synchronizace videa majitelem dosud nebyly dokončeny.

Server při aktivním přenosu spotřeboval přibližně 3 % jednoho jádra. Plánovaný limit pod 2 % nebyl splněn. Zkouška blokujícího UDP příjmu zhoršila CPU i jitter; dávkování dvou rámců neprokázalo úsporu a přidalo čekání. Obě změny byly vráceny. Pro další optimalizaci je potřeba profil CPU se stacky; není podložené přisuzovat náklady samotnému ALSA.

## Zbývající ověření na hardwaru

- Potvrzení čistého poslechu na vzdáleném receiveru a synchronizace s videem.
- Fyzické uspání a probuzení PC, odpojení a opětovné připojení síťového kabelu.
- Nezávislé měření celkové latence a snížení zátěže serveru pod plánovaný limit.

Během poslechu se tyto rušivé scénáře neprovádějí. Seznam fází a jejich stav je v [TODO.md](../TODO.md).
