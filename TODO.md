# Postup ověřování NKS AudioLinku

Stav se aktualizuje podle ověřených výsledků. Zaškrtnutí fáze vyžaduje i její skutečný test.

- [x] 0. Kostra řešení, místní sestavení a testy, veřejný repozitář na GitHubu a zelené CI pro Windows i Linux. Ověřeno 2026-10-02.
- [x] 1. Přenositelné jádro: UDP protokol, HMAC, vyrovnávací fronta, korekce rozdílných hodin a testy. Ověřeno 2026-10-02: sada jádra, serveru a fronty má 39 úspěšných testů a pokrytí řádků jádra 86,93 %. Simulace ±200 ppm proběhla pro 10 minut virtuálního času.
- [x] 2. UDP server a výstup do WAV nebo bez zvukové karty; test tónu přes síť a kontrola 440 Hz. Ověřeno 2026-10-02 mezi Windows a Linuxem x86_64: WAV 5,095 s, RMS 8 484, dominantní tón 440 Hz a žádné přetečení.
- [ ] 3. ALSA výstup, služba systemd a desetiminutový test na cílovém Linuxu. Předchozí přenos trval 600 s a při cílové frontě 30 ms neměl podle klienta ani serveru žádné chyby toku. Nová verze .NET 9.0.20 běžela 600 s se stabilní relací; všech 120 kontrol zastihlo ALSA ve stavu RUNNING a CPU bylo 1,277 % jednoho jádra. Při ručně nastavených 10 ms však čítače ztrát, pozdních rámců a podtečení vzrostly z 3 na 59. Novou verzi je třeba stejně dlouho ověřit také s frontou 30 ms.
- [x] 4. Windows klient WASAPI, tichý tok a skutečný zvuk. Tón zachycený do WAV, živý výstup PC přes grafickou aplikaci do vzdáleného ALSA i souvislý tichý tok byly ověřeny. Majitel potvrdil čistý poslech na receiveru.
- [ ] 5. Změřit celkovou latenci; cíl je pod 100 ms. Aplikace počítá se skutečnou délkou zachytávacího bufferu a při živém přenosu ukázala odhad 74 ms. Devět kliknutí z Windows přes LAN do WAV na Linuxu vyšlo 22,96–37,88 ms, medián 31,39 ms, od odeslání UDP paketu. Starší měření na smyčce cílového Linuxu vyšlo 27,36–28,69 ms, medián 28,22 ms. Nezávislé měření celé fyzické cesty a kontrola synchronizace videa zbývají.
- [x] 6. WPF aplikace, oznamovací oblast, nastavení, vyhledání serveru a snímky obrazovky. Ověřeno 2026-10-02: připojení a odpojení proti WAV serveru, živé statistiky, změna zesílení za běhu, vyhledání skutečné služby a snímek v `docs/screenshots`.
- [x] 7. Režim virtuální zvukovky a bezpečná obnova výchozího výstupu. Ověřeno 2026-10-02: samostatná instalace podepsaného VB-CABLE po souhlasu majitele bez restartu PC, skutečný 440 Hz přenos CABLE Input → CABLE Output → UDP WAV a obnova všech tří výchozích rolí Windows po odpojení i dalším spuštění po simulovaném pádu. Ovladač není součástí projektu.
- [ ] 8. Obnova po výpadku sítě, restartu serveru a uspání; test převzetí obsazeného serveru. Skutečný klient WASAPI po restartu služby obnovil přenos v LAN. Sedm Windows integračních testů pokrývá tichý tok, pozdní start a restart serveru, nové zachytávání po chybě zařízení, zastavení po odmítnutí a desetisekundový výpadek UDP s návratem stejné relace do 5 s. Integrační testy pokrývají také převzetí serveru a opětovné otevření výstupu. Fyzické odpojení kabelu a uspání a probuzení PC zbývají.
- [ ] 9. Vydání, README, historie změn a balíčky. Místní balíčky pro Windows a Linux byly sestaveny a zkontrolovány; Windows příkazový klient i Linux server se z nich spustily. Workflow umí ručně sestavit artefakty bez vydání. Tag `v0.1.0` zbývá po dokončení hardwarových kontrol.

## Pravidla

- Nevkládat do Git historie údaje z interního inventáře, přístupové údaje, lokální konfiguraci ani binárky cizího ovladače.
- Změny verzovat po smysluplných celcích jako Conventional Commits.
- Přímé ovládání receiveru přes jeho vyhrazené řídicí spojení nepoužívat.
