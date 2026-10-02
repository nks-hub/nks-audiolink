# Přenosový protokol NKS AudioLink v1

UDP, výchozí port **7355**. Všechna vícebajtová čísla jsou **little-endian**.
Jeden datagram obsahuje jeden paket. Paket má méně než 1 400 B, aby se v běžné síti nemusel dělit.

## Společná hlavička (16 B)

| Offset | Velikost | Pole | Poznámka |
|---|---|---|---|
| 0 | 4 | `magic` | ASCII `NKAL` |
| 4 | 1 | `version` | `1` |
| 5 | 1 | `type` | viz tabulka typů |
| 6 | 2 | `flags` | bitové příznaky, viz níže |
| 8 | 4 | `session` | náhodné číslo relace, volí klient při `HELLO` |
| 12 | 4 | `seq` | pořadové číslo paketu v relaci (pro statistiku ztrát) |

Příznak `AUTH` znamená, že paket končí **16 B ověřovací značkou**: prvními 16 B výsledku
HMAC-SHA256(psk, celý paket bez značky). Server s nastaveným klíčem paket bez platné značky
tiše zahodí. Značky porovnává v konstantním čase.

### Příznaky
| Bit | Název | Význam |
|---|---|---|
| 0 | `AUTH` | paket nese ověřovací značku HMAC |
| 1 | `SILENT` | (AUDIO) rámec je ticho, data se neposílají |
| 2 | `TAKEOVER` | (HELLO) převzít server, i když má jiného aktivního klienta |

## Typy paketů

| Kód | Typ | Směr |
|---|---|---|
| 1 | `HELLO` | klient → server |
| 2 | `AUDIO` | klient → server |
| 3 | `BYE` | klient → server |
| 4 | `STATS` | server → klient |
| 5 | `DISCOVER` | klient → broadcast |
| 6 | `DISCOVER_REPLY` | server → klient |
| 7 | `PING` | klient → server |
| 8 | `PONG` | server → klient |
| 9 | `REJECT` | server → klient |

### HELLO (tělo)
| Velikost | Pole |
|---|---|
| 4 | `sampleRate` (48000) |
| 1 | `channels` (2) |
| 1 | `format` (1 = s16le) |
| 1 | `frameMs` (5) |
| 2 | `targetLatencyMs` (např. 30) |
| 1 | délka jména N |
| N | jméno klienta UTF-8 (max 64 B) |

Klient posílá `HELLO` při startu a pak každé 2 s pro udržení relace. Server odpoví `STATS`
(přijato) nebo `REJECT`.

### AUDIO (tělo)
| Velikost | Pole |
|---|---|
| 8 | `sampleIndex`: index prvního vzorku rámce od začátku relace |
| 2 | `frameCount`: počet vzorků na kanál v rámci |
| 0 nebo frameCount × channels × 2 | PCM s16le prokládaně; při `SILENT` prázdné |

Server zařazuje rámce podle `sampleIndex`. Rámec se starším indexem, než je aktuální pozice
přehrávání, zahodí (počítá `late`). Mezeru v indexech po uplynutí času zaplní tichem
(počítá `lost`).

### BYE
Bez těla. Server okamžitě ukončí relaci a po `idleReleaseSec` uvolní zařízení.

### STATS (tělo, server → klient, 1×/s)
| Velikost | Pole |
|---|---|
| 1 | `accepted` (1/0) |
| 2 | `bufferMs`: aktuální zaplnění vyrovnávací fronty |
| 2 | `sinkDelayMs`: zpoždění výstupu (ALSA `snd_pcm_delay`) |
| 4 | `underruns` |
| 4 | `overruns` |
| 4 | `lost` |
| 4 | `late` |
| 4 | `ratioPpm`: aktuální korekce rozdílných hodin v ppm, číslo se znaménkem |
| 8 | `echoTicks`: časová značka posledního `PING` od klienta pro výpočet RTT; jinak 0 |

### DISCOVER / DISCOVER_REPLY
`DISCOVER` bez těla, posílá se na `255.255.255.255:7355` **a** na broadcast každého
aktivního IPv4 rozhraní (PC má víc adaptérů).
`DISCOVER_REPLY`: `nameLen(1) name`, `version(1)`, `sampleRate(4)`, `channels(1)`,
`authRequired(1)`, `sinkLen(1) sinkPopis`.

### PING / PONG
`PING`: `ticks(8)` = `Stopwatch.GetTimestamp()` klienta. `PONG` vrátí stejnou hodnotu → RTT.

### REJECT
`reason(1)`: 1 = busy, 2 = nepodporovaný formát, 3 = nepovolená adresa, 4 = chybná autentizace
(posílat jen pokud to nastavení serveru dovolí, jinak tiše zahodit).

## Chování v čase

```
klient                          server
  │ HELLO ───────────────────────▶ │ otevře sink, buffer prázdný
  │ ◀─────────────────────── STATS │ accepted=1
  │ AUDIO idx=0 ─────────────────▶ │
  │ AUDIO idx=240 ───────────────▶ │ ... plní buffer do targetLatencyMs, pak začne hrát
  │ AUDIO SILENT idx=… ──────────▶ │ ticho na PC: tok pokračuje, buffer drží úroveň
  │ HELLO (keepalive 2 s) ───────▶ │
  │ ◀─────────────────────── STATS │ každou sekundu
  │ BYE ─────────────────────────▶ │ konec relace
```

Pokud klient 3 s nepošle žádný paket, server relaci ukončí sám.
