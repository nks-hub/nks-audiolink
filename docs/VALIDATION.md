# Ověření

Stav k 2026-10-02. Lokální konfigurace a nezkrácené logy zůstávají mimo Git.

| Oblast | Výsledek |
|---|---|
| Přenositelné jádro, server a fronta klienta | 39 automatických testů prošlo: protokol, ověřování paketů, vyrovnávání síťových výkyvů, korekce rozdílných hodin, výstupy a převzetí obsazeného serveru |
| Pokrytí jádra | 86,93 % řádků a 76,01 % větví podle Cobertura; požadavek alespoň 80 % řádků splněn |
| Windows klient | 7 integračních testů přes místní UDP spojení prošlo: tichý tok, pozdní start, restart serveru, nové zachytávání po chybě zařízení, zastavení po odmítnutí obsazeného serveru nebo nepodporovaného formátu a desetisekundový výpadek UDP |
| Windows → Linux WAV | Tón 440 Hz, délka 5,095 s, RMS 8 484; nulová přetečení a pozdní rámce |
| Nasazený ALSA server se systemd | Předchozí přenos trval 600 s a při cílové frontě 30 ms neměl podtečení, přetečení, ztráty ani pozdní rámce. Nová verze běžela 600 s se stabilním procesem a relací; všech 120 kontrol zastihlo ALSA ve stavu RUNNING, záznam služby byl bez chyb. Při ručně zvolených 10 ms však přibyly chyby toku. |
| Uvolnění zařízení | Po odpojení klienta služba uvolnila ALSA zařízení po nastaveném intervalu |
| Skutečný WASAPI klient | Zachycený zvuk potvrzen ve WAV; přenos na LAN se obnovil po restartu systemd služby |
| Desetisekundový výpadek UDP | Po návratu server přijal stejnou relaci do 5 s a zvuk pokračoval bez nového zachytávání; fyzický kabel nebyl odpojen |
| Živá aplikace | Připojení, odpojení, vyhledání serveru, statistiky a změna zesílení během přenosu ověřeny |
| Virtuální režim | Skutečný 440 Hz signál CABLE Input → CABLE Output → UDP WAV potvrzen analýzou souboru |
| Obnova výchozího zařízení | Všechny tři role Windows obnoveny po odpojení i dalším startu po násilném ukončení aplikace |
| Poslech | Majitel potvrdil čistý zvuk na receiveru. Synchronizace s videem zatím potvrzena není. |
| Hlasitost | Na obou kanálech ovládacího prvku `Speaker` v hardwarovém směšovači byl útlum −10 dB; po úpravě na 0 dB a návratu zesílení v aplikaci z 200 % na 100 % majitel potvrdil, že je hlasitost v pořádku. Hlasitost zařízení CABLE i aktivní aplikace ve Windows byla 100 % a zvuk nebyl vypnutý. |
| Živý výstup PC | Nenulový signál na vybraném výstupním zařízení, klient připojený a fyzický ALSA výstup ve stavu RUNNING |
| Připravené balíčky | Windows příkazový klient se spustil z balíčku; Linux server jako jediný soubor spustil `--help` na cílovém hostiteli bez instalace .NET |

VB-CABLE byl nainstalován samostatným podepsaným instalátorem po souhlasu majitele. Testovací PC nebyl restartován. Ovladač ani instalátor nejsou součástí projektu.

## Latence a CPU

Grafická aplikace při živém přenosu ukazovala odhad 74 ms. Součet zahrnuje skutečnou délku bufferu při zachytávání, cílovou frontu klienta, rámcování, polovinu doby odezvy v síti a aktuální zaplnění fronty a zpoždění výstupu serveru. Nezahrnuje neznámé zpoždění za fyzickou zvukovou kartou. Fyzické měření celého řetězce a posouzení synchronizace videa dosud nebyly dokončeny.

První zkouška na cílovém Linuxu s původním runtime 9.0.4 použila skutečný UDP server se zápisem do WAV, oddělený místní port a devět kliknutí. Odeslání paketu i první nenulový vzorek byly označeny stejnými monotónními hodinami; výpočet zohlednil pozici vzorku v 5 ms rámci. Při cílové vyrovnávací frontě 30 ms vyšly prodlevy 27,36–28,69 ms, medián 28,22 ms. Server nezaznamenal podtečení, přetečení, ztráty ani pozdní rámce. Test nezahrnul Windows, fyzickou LAN ani ALSA.

Nové měření vedlo z Windows přes skutečnou LAN do serveru na Linuxu, který zaznamenal okamžik zápisu prvního nenulového vzorku do WAV. U devíti kliknutí vyšla doba od odeslání UDP paketu **22,9607–37,8828 ms**, medián **31,393 ms**. Odhad rozdílu hodin vycházel z 80 čtyřčasových síťových vzorků, 40 před měřením a 40 po něm. Nejnižší naměřená doba obousměrné odezvy byla 0,1064 ms před testem a 0,1167 ms po něm. Samotná možná asymetrie sítě přidává nejistotu přibližně ±0,05835 ms; časování operačních systémů může přidat další, nevyčíslenou odchylku. Měření nezahrnuje zachytávání přes WASAPI, ALSA ani receiver, a proto neudává celkovou fyzickou latenci.

Předchozí server při aktivním přenosu do ALSA spotřeboval přibližně 3 % jednoho jádra; plánovaný limit pod 2 % nesplnil. Zkouška blokujícího UDP příjmu zhoršila zátěž procesoru i kolísání časování. Dávkování dvou rámců nepřineslo prokazatelnou úsporu a přidalo čekání. Obě změny byly vráceny.

Následný profil podle `/proc` našel většinu spotřeby ve vláknech ThreadPoolu. Srovnání na izolovaném serveru bez zvukové karty se stejným runtime 9.0.20 a tokem 200 paketů PCM za sekundu použilo vždy 20 s zahřátí a 30 s měření:

| Nastavení | CPU jednoho jádra | Nejdelší mezera přijetí audia |
|---|---:|---:|
| Výchozí aktivní čekání vláken, běžné dokončení síťových operací | 3,87 % | 10,1 ms |
| Aktivní čekání vypnuté, běžné dokončení síťových operací | 2,37 % | 13,1 ms |
| Aktivní čekání vypnuté, dokončení v síťovém vlákně, jedno takové vlákno | 1,40 % | 5,6 ms |

Všechny izolované běhy měly nulová podtečení a pozdní rámce; odesílač nezmeškal žádný termín. Linuxová binárka a jednotka systemd používají poslední variantu. Po kontrole `systemd-analyze verify` byly aktivovány restartem služby. Windows klient obnovil relaci během jedné sekundy a místní konfigurace zůstala zachována.

Nový server pak běžel 600 s se skutečným ALSA výstupem. Proces i relace zůstaly stejné, všech 120 pravidelných vzorků zachytilo ALSA ve stavu RUNNING a záznam služby neobsahoval chybu. Zátěž procesoru byla **1,277 % jednoho jádra**, tedy pod plánovanou hranicí 2 %. Při uživatelem zvolené cílové frontě 10 ms však čítače ztrát, pozdních rámců a podtečení vzrostly z 3 na 59; přetečení zůstalo na nule. V posledních dvou minutách čítače dále nerostly. Tento běh není bezchybným dlouhodobým testem. Předchozí běh trval 600 s a při frontě 30 ms chyby neměl; se stejnou hodnotou se nová verze ještě neotestovala po celých 600 s.

## Zbývající ověření na hardwaru

- Posouzení synchronizace s videem; čistý poslech majitel již potvrdil.
- Fyzické uspání a probuzení PC, odpojení a opětovné připojení síťového kabelu.
- Nezávislé měření celkové latence a bezchybný dlouhý test nové verze s cílovou frontou 30 ms. Limit zátěže serveru pod 2 % byl splněn.

Během poslechu se tyto rušivé scénáře neprovádějí. Seznam fází a jejich stav je v [TODO.md](../TODO.md).
