# Ověření

Stav k 2026-10-02. Lokální konfigurace a nezkrácené logy zůstávají mimo Git.

| Oblast | Výsledek |
|---|---|
| Přenositelný Core/server a fronta klienta | 39 automatických testů prošlo; protokol, autentizace, jitter, drift, sinky a takeover |
| Pokrytí aktuálního Core | 86,93 % řádků a 76,01 % větví podle Cobertura; požadavek alespoň 80 % řádků splněn |
| Windows klient | 7 integračních testů přes UDP loopback prošlo: tichý tok, pozdní start, restart serveru, obnova zdroje po chybě capture, úplné zastavení po odmítnutí busy/unsupported a desetisekundový výpadek UDP |
| Windows → Linux WAV | Tón 440 Hz, délka 5,095 s, RMS 8 484; nulová přetečení a pozdní rámce |
| Nasazený ALSA/systemd server | Nejnovější souvislý 600s přenos: nula podtečení, přetečení, ztrát a pozdních rámců v klientských i závěrečných serverových statistikách |
| Uvolnění zařízení | Po odpojení klienta služba uvolnila ALSA zařízení po nastaveném intervalu |
| Skutečný WASAPI klient | Zachycený zvuk potvrzen ve WAV; přenos na LAN se obnovil po restartu systemd služby |
| Desetisekundový výpadek UDP | Po návratu server přijal stejnou relaci do 5 s a audio pokračovalo bez nové capture instance; fyzický kabel nebyl odpojen |
| Živé UI | Připojení, odpojení, discovery, statistiky a změna gain během přenosu ověřeny |
| Virtuální režim | Skutečný 440Hz signál CABLE Input → CABLE Output → UDP WAV potvrzen analýzou souboru |
| Obnova výchozího zařízení | Všechny tři role Windows obnoveny po odpojení i dalším startu po násilném ukončení aplikace |
| Živý výstup PC | Nenulový signál na vybraném render endpointu, klient připojený a fyzický ALSA výstup ve stavu RUNNING |
| Publikované balíčky | Windows CLI se spustilo z balíčku; Linux singlefile server spustil `--help` na cílovém hostiteli bez instalace .NET |

VB-CABLE byl nainstalován samostatným podepsaným instalátorem po souhlasu majitele. Testovací PC nebyl restartován. Ovladač ani instalátor nejsou součástí projektu.

## Latence a CPU

UI při živém přenosu ukazovalo odhad 74 ms. Součet zahrnuje skutečnou délku capture bufferu, cílovou frontu klienta, rámcování, polovinu RTT a aktuální buffer a delay serveru. Nezahrnuje neznámé zpoždění za fyzickou zvukovou kartou. Fyzické měření a posouzení synchronizace videa majitelem dosud nebyly dokončeny.

Další zkouška na cílovém Linuxu s původním runtime 9.0.4 použila skutečný UDP server s instrumentovaným WAV sinkem, oddělený loopback port a devět kliknutí. Odeslání paketu i první nenulový vzorek sinku byly označeny stejnými monotónními hodinami; výpočet zohlednil pozici vzorku v 5ms rámci. Při cílovém jitter bufferu 30 ms vyšly prodlevy 27,36–28,69 ms, medián 28,22 ms. Statistika serveru měla nulové podtečení, přetečení, ztráty i pozdní rámce. Jde o změřenou softwarovou část UDP → jitter/drift → WAV, bez Windows capture, fyzické LAN a ALSA. Neprokazuje celkovou fyzickou latenci.

Server při aktivním přenosu spotřeboval přibližně 3 % jednoho jádra. Plánovaný limit pod 2 % nebyl splněn. Zkouška blokujícího UDP příjmu zhoršila CPU i jitter; dávkování dvou rámců neprokázalo úsporu a přidalo čekání. Obě změny byly vráceny. Pro další optimalizaci je potřeba profil CPU se stacky; není podložené přisuzovat náklady samotnému ALSA.

Následný profil podle `/proc` našel většinu spotřeby v ThreadPool vláknech. Srovnání na izolovaném null serveru se stejným runtime 9.0.20 a 200Hz PCM tokem použilo vždy 20 s zahřátí a 30 s měření:

| Nastavení | CPU jednoho jádra | Nejdelší mezera přijetí audia |
|---|---:|---:|
| Výchozí otáčení ThreadPoolu, běžné socket dokončení | 3,87 % | 10,1 ms |
| Limit otáčení 0, běžné socket dokončení | 2,37 % | 13,1 ms |
| Limit 0, inline socket dokončení, jedno socket vlákno | 1,40 % | 5,6 ms |

Všechny běhy měly nulová podtečení a pozdní rámce; odesílač nezmeškal žádný termín. Linux runtimeconfig a systemd jednotka používají poslední variantu. Aktualizovaná binárka a jednotka prošly kontrolou `systemd-analyze verify` a byly aktivovány restartem služby. Windows klient obnovil relaci během jedné sekundy; fyzický ALSA výstup přešel do RUNNING a statistiky zůstaly nulové. Lokální konfigurace zůstala zachována. Výsledek null sinku neprokazuje limit pod 2 % s fyzickým ALSA; desetiminutové měření nové verze s ALSA probíhá zvlášť.

## Zbývající ověření na hardwaru

- Potvrzení čistého poslechu na vzdáleném receiveru a synchronizace s videem.
- Fyzické uspání a probuzení PC, odpojení a opětovné připojení síťového kabelu.
- Nezávislé měření celkové latence a snížení zátěže serveru pod plánovaný limit.

Během poslechu se tyto rušivé scénáře neprovádějí. Seznam fází a jejich stav je v [TODO.md](../TODO.md).
