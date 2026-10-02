# NKS AudioLink — postup práce

Stav se aktualizuje podle ověřených výsledků. Zaškrtnutí fáze vyžaduje i její skutečný test.

- [x] 0. Kostra řešení, lokální build/test, soukromý GitHub repozitář a zelené CI. Ověřeno 2026-10-02: CI Windows i Linux uspělo.
- [x] 1. Core: UDP protokol, HMAC, jitter buffer, korekce driftu a testy. Ověřeno 2026-10-02: aktuální sada Core/server/fronta má 39 úspěšných testů, pokrytí řádků Core 86,93 %; simulace driftu ±200 ppm po 10 minut.
- [x] 2. UDP server a sinky WAV/null; test tónu přes síť a kontrola 440 Hz. Ověřeno 2026-10-02 mezi Windows a Linux x86_64: WAV 5,095 s, RMS 8 484, dominantní 440 Hz, 0 přetečení.
- [ ] 3. ALSA sink, systemd nasazení a desetiminutový test na cílovém Linuxu. Dosavadní 600s test přes systemd: 0 podtečení/přetečení/ztrát/pozdních rámců v klientovi i závěrečném záznamu serveru. Nová binárka .NET 9.0.20 a optimalizovaná jednotka jsou aktivní; klient po restartu služby obnovil relaci během jedné sekundy. Izolovaný null test snížil CPU z 3,87 % na 1,40 %; skutečná ALSA a opakovaný 600s test se ověřují zvlášť, potvrzení poslechu zbývá.
- [ ] 4. Windows WASAPI klient, tichý tok a ověření skutečného zvuku. Zachycený tón do WAV a živý výstup PC přes UI do vzdáleného ALSA ověřeny; čeká se na potvrzení poslechu. Test souvislého tichého toku prošel.
- [ ] 5. Změřit a zapsat latenci; cílově pod 100 ms. UI počítá se skutečnou periodou capture; při živém přenosu odhad 74 ms. Na cílovém hostiteli změřeno devět kliknutí UDP loopback → WAV: 27,36–28,69 ms, medián 28,22 ms při cílovém bufferu 30 ms. Fyzické měření celého řetězce a kontrola synchronizace videa zbývají.
- [x] 6. WPF UI, tray, nastavení, discovery a snímky obrazovky. Ověřeno 2026-10-02: připojení/odpojení UI proti WAV serveru, živé statistiky, změna gain během přenosu, discovery proti skutečné službě a screenshot v docs/screenshots.
- [x] 7. Režim virtuální zvukovky a bezpečná obnova výchozího výstupu. Ověřeno 2026-10-02: samostatná instalace podepsaného VB-CABLE po souhlasu majitele bez restartu PC, skutečný 440Hz přenos CABLE Input → CABLE Output → UDP WAV a obnova všech tří výchozích rolí Windows po odpojení i dalším spuštění po simulovaném pádu. Ovladač není součástí projektu.
- [ ] 8. Obnova po výpadku sítě, restartu serveru a uspání; test takeover. Skutečný WASAPI klient obnovil přenos na LAN po restartu služby serveru. Sedm Windows integračních testů pokrývá tichý tok, pozdní start serveru, restart serveru, výměnu capture po chybě zařízení, úplné zastavení odmítnutého klienta a desetisekundový výpadek UDP s návratem stejné relace do 5 s. Serverový takeover a opětovné otevření exkluzivního sinku pokrývají integrační testy. Fyzické odpojení kabelu a uspání/probuzení PC zbývají; během poslechu se neprovádějí.
- [ ] 9. Release workflow, kompletní README, changelog a ověřené artefakty. Lokální Windows a Linux balíčky sestaveny a struktura ověřena, publikované Windows CLI i Linux server spustily help. Workflow má ruční sestavení artefaktů bez vydání; tag v0.1.0 zbývá po dokončení hardwarových kontrol.

## Pravidla

- Nevkládat do Git historie údaje z interního inventáře, přístupové údaje, lokální konfiguraci ani binárky cizího ovladače.
- Změny verzovat po smysluplných celcích jako Conventional Commits.
- Přímé ovládání receiveru přes jeho vyhrazené řídicí spojení nepoužívat.
