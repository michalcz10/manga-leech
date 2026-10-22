# Revize projektu Manga Leech

> Nálezy níže popisují stav před implementací. Opravy a výsledky jejich ověření
> jsou zaznamenané v závěrečné části „Implementace poznatků“.

Datum: 22. 10. 2026. Zaměření: rychlost, velikost PDF, spolehlivost a testy.
Revidovány všechny verzované zdrojové soubory, testy, závislosti a dokumentace.
Aplikační kód nebyl změněn. Síťový výkon proti živému serveru nebyl měřen.

## Hlavní závěr

Současná cesta pro JPEG je již velmi efektivní: obrázky se nepřekódují ani
nerozbalují do pixelů při tvorbě PDF, ASCII85 je vypnuté. Největší příležitosti
jsou opakované používání HTTP spojení, omezení restartů PDF procesů a zvláštní
zpracování ostatních obrazových formátů. Další výrazné zmenšení JPEG kapitol
vyžaduje optimalizaci samotných obrázků; změna fyzických rozměrů PDF stránky
jejich datový objem nesníží.

## Nálezy

### 1. [P2] Poškozený JPEG projde kontrolou a zveřejní se v PDF

Místa: `manga_leech.py:384–389`, `493–494`, `394–404`.

`Image.verify()` v použité JPEG cestě nekontroluje dekódovatelnost obrazových
dat. JPEG uříznutý na polovinu prošel `valid_image()` a `make_pdf()` vytvořilo
PDF bez chyby, přestože `Image.load()` hlásí neúplný soubor. Problém nastane
například při poškozené cache nebo pokud server odešle neúplný JPEG se správnou
délkou HTTP odpovědi. Kontrola Content-Length jej v těchto případech nezachytí.
Existující PDF se následně standardně přeskakuje.

Doporučení: při prvním stažení skutečně ověřit dekódování před uložením cache.
Pro rychlé další použití lze uchovávat důkaz ověření s kontrolním součtem
obsahu; současný volitelný manifest otiskuje URL, nikoliv bajty obrázků.
Starou cache bez ověření zkontrolovat jednorázově. Dekódování nepatří zpět do
každého vkládání JPEG do PDF. Přidat test zkráceného, ale rozpoznatelného JPEG.

### 2. [P2] HTTP klient nepoužívá existující spojení

Místa: `manga_leech.py:281–287`, `302`.

Opener uložený pro každé vlákno není connection pool. Ověřeno ve zdroji
instalovaného `urllib.request.AbstractHTTPHandler.do_open`: při každém volání
vytvoří HTTPConnection a nastaví `Connection: close`. Stovky obrázků ze stejného
hostu proto opakují navázání TCP/TLS; dopad roste s latencí a malými obrázky.

Doporučení: klient s podporou opakovaného použití spojení, omezený počtem
download vláken. Zachovat cookies, Referer, kontrolu úplnosti odpovědi,
celkový časový limit a společné čekání po 429/503. Sdílený download executor
napříč kapitolami umožní zachovat také klienty vláken. Skutečné zrychlení
ověřit na serveru; tato revize mu nepřiřazuje neověřené procento.

### 3. [P2] Restart PDF procesu po každé kapitole dominuje obnově z cache

Místo: `manga_leech.py:556–558`.

`max_tasks_per_child=1` uvolňuje paměť, ale opakovaně spouští interpreter a
importuje knihovny. Na stejných devíti lokálních kapitolách, s jedním workerem,
trvala dávka 4,85 s při recyklaci po každé kapitole a 1,02 s při použití jednoho
procesu pro celou dávku. Samotné renderování zabralo 0,81 s, respektive 0,61 s.
Měření zahrnuje start a ukončení poolu; nejde o zrychlení celého stahování.

Doporučení: nastavitelný počet kapitol na proces nebo zvláštní režim obnovy
z cache. Rozumnou hodnotu určit měřením špičky RAM a jejího vývoje během delší
dávky; nevypínat recyklaci bez kontroly paměti. Test dnes výslovně vyžaduje
jiný PID pro každou úlohu a musel by respektovat novou konfiguraci.

### 4. [P2] Obecná cesta pro ostatní formáty zvětšuje data

Místo: `manga_leech.py:432–441`.

Každý formát kromě JPEG prochází dekódováním přes ImageReader a Flate kompresí.
Jednobitový PNG se navíc změní na 8bitové RGB. Kontrolní vzorky:

| Vstup | Velikost vstupu | Velikost PDF | Poznámka |
|---|---:|---:|---|
| WebP | 126 770 B | 1 348 960 B | 10,64×; vzorek vytvořený z místního JPEG |
| Jednobitový PNG | 3 762 B | 13 340 B | 3,55×; syntetická černobílá kresba |

U PNG bylo přímo ověřeno `/DeviceRGB` a 8 bitů na složku v PDF. Poměry obsahují
i režii jednostránkového PDF a nejsou odhadem pro všechny série. Prohlédnutá
reálná cache obsahovala pouze JPEG, takže tento problém nevysvětluje její velikost.

Doporučení: zachovat jednobitové/paletové reprezentace, případně využít
komprimovaný PNG stream tam, kde je kompatibilní s PDF. Pro WebP a jiné
fotografické podklady nabídnout výslovně volitelný JPEG převod s nastavením
kvality; ten znamená další ztrátovou kompresi a nesmí potichu změnit současný
slib zachování kvality. Zachovat průhlednost a doplnit testy režimů `1`, `P`,
`L`, RGB a RGBA. Výchozí přímé vkládání JPEG ponechat.

### 5. [P2] Některé HTTP chyby mohou zastavit postup na hodiny

Místo: `manga_leech.py:318–332`.

Retry-After se čte pro všechny opakované HTTP chyby, ale limit 120 sekund se
kontroluje jen pro 429 a 503. HTTP 500, 502 nebo 504 s Retry-After 86400 tak
pošle vlákno do denního spánku mimo limit `--timeout`. Kapitola čeká na toto
vlákno a další kapitoly nezačnou. Reprodukce s mockovaným sleep ověřila
požadavek na spánek 86 400 sekund; skutečný spánek se nespouštěl.

Doporučení: uplatnit limit na všechny retryable statusy a ověřovat konečná
číselná Retry-After. Také znovu zkontrolovat host cooldown těsně před požadavkem:
nyní může během per-thread `--delay` jiné vlákno nastavit novou pauzu.

## Další menší příležitosti

- Přímé `--chapter` načte stránku pro titul v `main` (617), následně ji znovu
  načte přes `download_chapter`/`chapter_html` (478, 342). První odpověď lze
  předat dál; ušetří se jeden GET a prodleva na spuštění.
- `Client.get` drží seznam chunků a následně jejich spojenou kopii (303–316).
  U velkých obrázků a více vláken by zápis do `.part` během přenosu snížil
  špičku RAM; po dokončení ověřit a atomicky přejmenovat.
- Identita JPEG XObjectu používá cestu (398), takže stejné bajty pod různými
  URL/cestami se v PDF opakují. Již vypočtený hash obsahu by umožnil deduplikaci
  bez dekódování. Přínos závisí na skutečných duplicitách.
- Výchozí prodleva 0,5 s a 4 download vlákna představují jen na čekání pro
  205 obrázků přibližně 26 sekund. Jde o záměrné chování, nikoli chybu.
  Zvyšování počtu vláken bez měření 429 a latence nemusí přinést zrychlení.
- Závislosti mají pouze dolní hranici, ale renderer používá interní API
  ReportLabu. Uchovávat ověřené verze a testovat jejich aktualizace; README
  uvádí Python 3.15, zatímco zde dostupné prostředí je 3.12.

## Ověření

- 29/29 existujících testů prošlo; dočasná složka byla nastavena do projektu
  kvůli omezení oprávnění sandboxu, bez změny testů.
- Prostředí: Windows, Python 3.12.14, Pillow 12.3.0, ReportLab 5.0.1,
  pypdf 6.21.0. Python 3.15 nebyl tímto auditem testován.
- Referenčních 205 JPEG: vstup 13 425 651 B, PDF 13 581 329 B (+1,16 %).
  Medián tří lokálních renderů 0,163 s. Všechny vložené JPEG streamy odpovídají
  původním obrázkům bajt po bajtu, pořadí a počet stran souhlasí.
- Srovnání procesů je jedno kontrolní měření každé varianty nad devíti
  kapitolami z cache. Neobsahuje síť, není zárukou obdobného výsledku jinde.
- Reprodukční skript: `tmp/review_benchmark.py`; výsledky:
  `tmp/review-audit/results.json`. Adresář `tmp` je ignorovaný Gitem.
- Dočasná PDF z benchmarku byla po analýze odstraněna; stažené obrázky a
  uživatelova PDF zůstaly beze změny. Skript je při dalším spuštění vytvoří.

## Doporučené pořadí úprav

Nejprve opravit validaci JPEG a neomezený Retry-After. Pro síťový běh následně
zavést opakované používání HTTP spojení, pro obnovu z cache snížit četnost
restartů procesů. Nakonec doplnit efektivní větve pro ostatní obrazové formáty
a případný volitelný režim menších PDF. Pro současnou JPEG cache je hlavním
kompromisem dalšího výrazného zmenšení velikost versus kvalita obrazu.

## Implementace poznatků (22. 10. 2026)

Všech pět hlavních nálezů bylo zapracováno:

- Úplná kontrola dekódování nových a starých neověřených obrázků; opakované
  použití cache porovnává SHA-256 s atomicky uloženým souborem `.verified`.
- Requests Session pro každé vlákno, sdílené cookies, trvalý download pool
  napříč kapitolami, uzavření relací na konci běhu. Obrázky se streamují na disk.
- Recyklace PDF procesů po nastavitelné dávce (`--pdf-tasks-per-child`, výchozí
  10) a explicitní uvolnění cyklických objektů po každém PDF.
- Přímé vkládání kompatibilních PNG IDAT streamů, zachování bitové hloubky
  a palety. Ostatní vhodné formáty používají PNG kompresi; převod do JPEG
  je výslovně volitelný (`--jpeg-quality`). Původní JPEG zůstávají beze změny.
- Limit Retry-After pro všechny retryable statusy, odmítnutí nekonečných hodnot
  a kontrola společné pauzy až po per-thread prodlevě.

Dále byla přidána deduplikace JPEG podle obsahu, přestavění PDF z cache přes
`--rebuild`, použití prvního HTML při přímém `--chapter`, zohlednění kvality
v PDF manifestu a zneplatnění starého manifestu při přestavění bez něj.
Při testování byly opraveny také PNG transparentní barvy ve stupních šedi a RGB.
Dokumentace popisuje nové volby a skutečné závislosti; ověřené verze jsou
uložené v `requirements-tested.txt`.

Ověření: 46/46 testů na Windows/Pythonu 3.12. Integrační testy používají lokální
HTTP server, včetně kontroly stejného TCP spojení napříč kapitolami. PDFium
porovnává vykreslené PNG pixely, další test kontroluje původní 16bitové vzorky.
Všech 205 JPEG streamů referenční kapitoly a rozměry jejích stran byly znovu
porovnány se zdrojem; souhlasí. `pip check` a `git diff --check` prošly.

| Měření | Před implementací | Po implementaci |
|---|---:|---:|
| 9× stejná 205stránková JPEG kapitola, jeden PDF worker | 5,39 s | 2,52 s |
| Jednobitový PNG: velikost PDF | 13 337 B | 6 105 B |
| WebP vzorek: bezeztrátové PDF | 1 348 957 B | 1 037 085 B |
| WebP vzorek: PDF s volitelnou kvalitou JPEG 85 | — | 155 951 B |

Jde o nové srovnání původního kódu z HEAD a upraveného kódu na stejných datech.
Dávka obsahuje devět opakování referenční kapitoly, nikoliv původních devět
různých kapitol z prvního auditu. Síť ani validace download cache nejsou
součástí měření PDF procesů. Samotné renderování jedné kapitoly včetně úklidu
má po změně malou režii navíc (mediány 0,203 s → 0,247 s); zrychlení celé dávky
plyne z méně častého spouštění procesů. Výchozí JPEG PDF zůstává kolem 13,58 MB.

Při třiceti renderováních se worker po každých deseti úlohách skutečně vyměnil.
Paměť po úklidu byla kolem 38–39 MiB, špička pod 80 MiB. Tato hodnota platí
pro měřený JPEG vzorek a PDF worker, nikoliv pro celý downloader nebo jiné série.
Skript a podrobné výsledky jsou v ignorované složce
`tmp/optimization_benchmark.py` a `tmp/optimization-benchmark/results.json`.
