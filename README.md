# Manga Leech

Python aplikace pro stažení kapitol z celé stránky série WeebCentral nebo RSS/Atom feedu a vytvoření jednoho PDF pro každou kapitolu. Obrázky vybírá pouze ze sekce `#chapter-images`, v pořadí čtečky. Každý obrázek tvoří jednu stránku PDF s původním poměrem stran, bez ořezu; dlouhé webtoonové obrázky se nedělí na A4.

## Instalace (Windows / PowerShell)

Potřebujete Python 3.11 nebo novější a virtuální prostředí. Aktuální aplikace a testy byly ověřeny na Pythonu 3.12:

```powershell
py -3 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

Stahování používá [Requests](https://requests.readthedocs.io/en/stable/user/advanced/) s opakovaným používáním HTTP/TLS spojení. Každé download vlákno má vlastní relaci, cookies jsou společné celému běhu. Vlákna i spojení se používají napříč kapitolami a při ukončení se zavřou. Přesměrování se zpracují automaticky. Prohlížeč ani Playwright nejsou potřeba. Po aktualizaci programu znovu spusťte instalaci `requirements.txt`, protože přibyly HTTP závislosti.

Přesné verze ověřeného prostředí včetně testovacích knihoven jsou v `requirements-tested.txt`. Pro běžné použití stačí `requirements.txt`.

## Instalace (Linux)

Příkazy spouštějte ve složce projektu, kde jsou `manga_leech.py` a `requirements.txt`. Je potřeba Python 3.11 nebo novější; testy byly ověřeny na Pythonu 3.12. Verzi dostupného Pythonu zjistíte pomocí `python3 --version`.

Na Debianu / Ubuntu nejprve nainstalujte Python a podporu virtuálních prostředí:

```bash
sudo apt update
sudo apt install python3 python3-venv python3-pip
```

Na ostatních distribucích nainstalujte odpovídající balíčky jejich správcem balíčků. Potom vytvořte virtuální prostředí a nainstalujte závislosti:

```bash
python3 -m venv .venv
./.venv/bin/python -m pip install -r requirements.txt
```

Spuštění celé série se souběžným stahováním a tvorbou PDF:

```bash
./.venv/bin/python manga_leech.py "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/The-Greatest-Estate-Developer" --workers 7 --pdf-workers 1
```

Pro pouhý seznam přidejte `--list`, pro zkušební stažení prvního vydání `--limit 1`. PDF se ukládají do `downloads/`, pokud nezadáte jinou složku přes `-o`.

Aktivace prostředí je volitelná. Pokud chcete místo celé cesty používat jen `python`:

```bash
source .venv/bin/activate
python manga_leech.py "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/The-Greatest-Estate-Developer" --list
deactivate
```

Závislosti instalujte do virtuálního prostředí bez `sudo pip`. Prohlížeč, Playwright ani externí program pro tvorbu PDF nejsou potřeba.

## Použití

```powershell
# Celá série od první kapitoly
.\.venv\Scripts\python.exe manga_leech.py "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/The-Greatest-Estate-Developer"

# Ověření seznamu celé série bez stahování obrázků
.\.venv\Scripts\python.exe manga_leech.py "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/The-Greatest-Estate-Developer" --list

# Všechny kapitoly ve feedu, jedno PDF na kapitolu
.\.venv\Scripts\python.exe manga_leech.py "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/rss"

# Nejdříve pouze seznam
.\.venv\Scripts\python.exe manga_leech.py "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/rss" --list

# Zkušebně první kapitola z feedu přes HTTP
.\.venv\Scripts\python.exe manga_leech.py "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/rss" --limit 1 -o downloads
```

Pokud server vrací HTTP 403, odmítá daný požadavek. Aplikace nespouští JavaScript ani neřeší CAPTCHA nebo přihlášení. Vlastní identifikaci HTTP klienta lze nastavit pomocí `--user-agent "MujKlient/1.0"`.

URL série se rozpozná automaticky a aplikace načte úplný seznam odkazovaný tlačítkem „Show All Chapters“. U uvedené série bylo ověřeno 225 položek, seřazených od kapitoly 1. Pokud odkaz na úplný seznam chybí, použije se seznam přímo ze sekce `#chapter-list` (krátké série tlačítko Show All nemají). Odkazy mimo tuto sekci se nepoužijí. Pokud chybí obojí nebo je seznam prázdný, aplikace ohlásí chybu. `--limit 1` zde vybere kapitolu 1.

RSS je seznam posledních vydání, nikoli archiv celé série. Při ověření obsahoval uvedený feed 20 položek (kapitoly 204–223). Při zadání RSS aplikace zpracuje pouze obsah feedu; pro celý archiv zadejte URL série.

- Výchozí řazení je číselně od nejnižší kapitoly (1, 2, 2.5, 10), poté se aplikuje `--limit`. `--oldest-first` je ponecháno jako alias.
- `--newest-first`: řazení od nejvyšší kapitoly.
- `--chapter URL`: stáhne přímo jednu kapitolu místo RSS.
- `--force`: stáhne znovu obrázky i existující PDF.
- `--rebuild`: přestaví i existující PDF z ověřené cache. Stahuje pouze chybějící nebo poškozené obrázky; stránku kapitoly stále načte pro aktuální seznam stran.
- `--jpeg-quality 85`: volitelná ztrátová komprese vhodných ne-JPEG obrázků, rozsah 1–95. Výchozí stav je bezeztrátový. Původní JPEG, jednobitové/paletové obrázky a průhlednost se touto volbou nepřekódují. Nová JPEG varianta se použije pouze tehdy, je-li její obrazový stream menší než bezeztrátová varianta.
- `--write-manifest`: zapne vytváření a kontrolu souborů `.pages.sha256` vedle PDF; výchozí stav je vypnuto.
- `--workers 4`: počet současně stahovaných obrázků, výchozí 4. `--workers 1` stahuje postupně. Počet není vázaný na CPU, protože stahování čeká hlavně na síť.
- `--pdf-workers 1`: počet samostatných procesů pro tvorbu PDF, výchozí 1. PDF se generuje na pozadí souběžně se stahováním další kapitoly. Hodnota 2 umožní tvořit dvě PDF současně a vyžaduje více paměti.
- `--pdf-tasks-per-child 10`: počet PDF před restartem procesu, výchozí 10. Hodnota 1 obnoví restart po každé kapitole za cenu pomalejšího dávkového zpracování.
- `--delay 1`: prodleva před každým požadavkem v jednotlivém vlákně, výchozí 0,5 s. Nejde o společný limit všech vláken; více vláken zvýší celkový počet požadavků za sekundu.
- `--timeout 60`: časový limit jednoho pokusu o přenos v sekundách. Čtení kontroluje i celkovou dobu, nejen nečinnost socketu. Jedno blokující čtení může limit překročit nejvýše přibližně o 10 s; systémové DNS nemusí tento limit respektovat.

Kapitoly se stahují postupně; uvnitř každé kapitoly se obrázky stahují paralelně. Po dokončení obrázků se kapitola předá samostatnému PDF procesu a ihned začne stahování další kapitoly. Proces se standardně recykluje po 10 PDF; po každém PDF se uvolní také cyklické objekty knihoven. PDF zachovává původní pořadí čtečky. Fronta má nejvýše dvojnásobek `--pdf-workers` nedokončených PDF; při zaplnění stahování počká. Procesům se předávají pouze cesty k obrázkům. Před úspěšným ukončením aplikace počká na všechna PDF; chyba tvorby PDF vede k návratovému kódu 1. Při přerušení se zruší čekající úlohy a běžící práce doběhne, což může chvíli trvat. Ověřené obrázky zůstávají pro další spuštění.

```powershell
# Vlastní počet vláken
.\.venv\Scripts\python.exe manga_leech.py "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/The-Greatest-Estate-Developer" --workers 8 --pdf-workers 2
```

PDF se ukládají do `downloads/`. Ověřené obrázky zůstávají v `downloads/.images/`, aby další spuštění mohlo navázat. Obrázky se stahují přímo do `.part` souborů a před zveřejněním cache se ověří struktura i úplné dekódování. Vedle obrázku vznikne malý soubor `.verified` s verzí kontroly a SHA-256 obsahu. Při dalším použití stačí ověřit hash; obrázek se znovu nedekóduje. Stará cache bez tohoto souboru projde jednorázovou úplnou kontrolou, takže první přestavění může být pomalejší.

Standardně se hotové PDF přeskakuje podle existence souboru a žádný `.pages.sha256` nevzniká. S volbou `--write-manifest` se kontroluje otisk verze generátoru, nastavení kvality, názvu vydání a seznamu URL; chybějící nebo nesouhlasící otisk vyvolá nové vytvoření PDF z cache. Tento manifest nekontroluje obsah již hotového PDF. Při přestavění bez manifestu se starý otisk odstraní, aby nepotvrdil jinou verzi PDF; při pouhém přeskočení PDF zůstane. Neúplná kapitola se jako PDF nezveřejní; chyba jedné kapitoly nezastaví ostatní. Návratový kód 0 znamená úspěch, 1 chybu, 130 přerušení. Poškozené nebo zastaralé hotové PDF obnovíte pomocí `--rebuild`; `--force` navíc vynutí nové stažení všech obrázků.

## Testy

```powershell
.\.venv\Scripts\python.exe -m pip install -r requirements-dev.txt
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
```

Aktuální sada 46 testů ověřuje parsery a řazení, názvy a metadata, pořadí a rozměry stran, souběh stahování a PDF procesů, jejich recyklaci a obnovu z cache. Regresní testy kontrolují zkrácené JPEG, ověřovací otisky, přestavění PDF a změnu kvality. Lokální HTTP server ověřuje opakované použití spojení mezi kapitolami, cookies mezi vlákny, přesměrování, Referer a odmítnutí neúplných přenosů. Testy nepotřebují přístup na WeebCentral, ale prostředí musí povolit loopback spojení.

PDFium porovnává vykreslené pixely jednobitových, paletových a dalších PNG; další testy kontrolují 16bitové vzorky, průhlednost, deduplikaci a původní JPEG streamy bajt po bajtu. Testovací renderer není potřeba pro běžné spuštění programu. Ověřené prostředí: Windows, Python 3.12, verze závislostí v `requirements-tested.txt`; Python 3.15 ani Linux nebyly v této revizi spuštěny.

## Referenční kapitola 1

RSS obsahuje pouze kapitoly 204–223, proto po seřazení začne kapitolou 204. Kapitolu 1 lze stáhnout přímo:

```powershell
.\.venv\Scripts\python.exe manga_leech.py --chapter "https://weebcentral.com/chapters/01J76XZ3W1SDBG0SAGFG8RYXS4"
```

Opravený parser zpracovává i samouzavírací značky `<img />` a u referenční kapitoly načítá všech 205 obrázků.

## Velikost PDF

JPEG obrázky se vkládají v původní komprimované podobě bez překódování a bez ztráty kvality. Stejné JPEG bajty pod různými URL se v jednom PDF uloží pouze jednou, ale všechny stránky zůstanou zachované. Formát se rozpoznává podle obsahu i u cache souborů s příponou `.img`. Neprokládané PNG bez průhlednosti používají přímo původní komprimovaný IDAT stream, včetně jednobitových, paletových a 16bitových obrázků. Další vhodné formáty se převádějí na PNG bezeztrátově; průhlednost se zachovává přes masku. Rozměry obrázků se nemění. Binární obrazové streamy se zbytečně nekódují přes ASCII85.

JPEG kapitoly zůstávají velikostí blízko původním souborům. U WebP a dalších ztrátových formátů může být bezeztrátové PDF stále výrazně větší než zdroj. Pokud přijmete další ztrátu kvality, použijte například `--jpeg-quality 85`; u existujících PDF přidejte `--rebuild` nebo kontrolu manifestu. Volba nemění obrázky v cache ani původní JPEG.

S volbou `--write-manifest` je verze způsobu vytváření PDF a nastavení kvality součástí otisku dokončené kapitoly; nesouhlasící PDF se přestaví z ověřené cache. Bez této volby se existující PDF neaktualizují automaticky. Pro přestavění přidejte `--rebuild` ke stejnému příkazu a ponechte obrázky v `.images/`.

## Čekání na poslední obrázky

Log obsahuje čas, číslo čekajícího obrázku a při selhání také jeho URL. Pokud se 10 sekund nedokončí žádný obrázek, aplikace vypíše seznam čekajících obrázků. Číslo v běžném výpisu znamená počet dokončených obrázků; poslední čekající soubor nemusí být poslední stránkou kapitoly.

Každý přenos má nejvýše tři pokusy. Chyby a číslo pokusu se vypisují. Síťové čtení má limit nečinnosti nejvýše 10 s, celkový přenos je navíc omezen pomocí `--timeout`. Pomalé průběžné posílání dat tak nemůže neomezeně obnovovat timeout. Neúplná odpověď s nesouhlasícím Content-Length se opakuje.

Při HTTP 429 nebo 503 klient respektuje Retry-After (sekundy nebo HTTP datum) a nastaví společnou pauzu pro další požadavky všech vláken na stejný host. Pauza se kontroluje po vlastní prodlevě vlákna, těsně před požadavkem. Již běžící požadavky doběhnou. Limit Retry-After 120 s platí pro všechny opakované stavy (429, 500, 502, 503, 504); delší pauza požadavek ukončí s vysvětlením. Nečíselné a nekonečné hodnoty používají běžný krátký backoff. Pro nezotavitelné selhání se nevytvoří neúplné PDF; ověřené obrázky zůstávají v cache a další kapitoly mohou pokračovat.

Pro diagnostiku lze použít `--workers 2 --timeout 30`. Zaznamenané HTTP 429 potvrzuje omezení serverem; samotné čekání na poslední obrázek jeho příčinu nepotvrzuje.

## Metadata pro Kavitu

PDF obsahuje vedle běžného `/Title` také XMP metadata podle Calibre:

- `/Title` i XMP `dc:title`: pouze název série, například `Overgeared`.
- `calibre:series`: společný název série, například `The Greatest Estate Developer`.
- `calibreSI:series_index`: číselný index vydání, například `1` nebo `104.1`.

Kavita používá `calibre:series` pro seskupení; pokud chybí, může použít celý titul kapitoly a vytvořit samostatné série. Dle dokumentace Kavita mapuje `series_index` na Volume, nikoli samostatné pole Chapter. Zde je použit jako pořadí vydání v rámci série. PDF se ukládají jako `Overgeared - Ch 025.pdf`, kapitola 0 jako `Overgeared - Ch 000.pdf` a desetinná kapitola jako `Overgeared - Ch 104.1.pdf`. Číslo je doplněno na nejméně tři číslice a nemá hash. Hash zůstává pouze v interní cache, aby se využily již stažené obrázky. Pokud nelze určit sérii a číslo, použije se původní bezpečný název s hashem. Dvě rozdílné kapitoly se stejným cílovým názvem jsou odmítnuty místo vzájemného přepsání. Série se odvozuje z části názvu před posledním `Chapter číslo`, `Chapter. číslo`, `Page číslo` nebo `Page. číslo`. U přímého `--chapter` se skutečný název načítá z titulku stránky. Pokud název nemá rozpoznatelnou sérii a číslo kapitoly, zapíše se pouze titul; série se nevymýšlí.

Dokumentace: https://wiki.kavitareader.com/guides/metadata/pdfs/ a https://wiki.kavitareader.com/guides/scanner/pdf/

Starší PDF ve výstupní složce se při spuštění s `--write-manifest` přestaví z ověřených obrázků v cache, pokud jim chybí aktuální otisk. Bez manifestu se existující PDF přeskakují. Nepoužívejte `--force`, pokud nechcete znovu stahovat obrázky. Pro PDF již přesunutá na server je nutné aktualizované soubory nahradit nově vytvořenými. Poté spusťte nové skenování příslušné série v Kavitě. Metadata byla ověřena čtením výsledných PDF, včetně zachování původních JPEG streamů; běh na konkrétní instalaci Kavity zde nebyl ověřen.

Při migraci se staré PDF s hashem ve výstupní složce odstraní až po úspěšném vytvoření nové verze se správným názvem a metadaty. Obrázky v cache zůstávají. Soubory přesunuté mimo výstupní složku se automaticky nemění.

## Série s označením Page

Položky `Page 1` i `Page. 1` se zpracují stejně jako `Chapter 1`: číselné řazení, výběr kapitoly přes přímou URL, RSS a metadata. Jde o název vydání v seznamu série; nemění to výběr jednotlivých obrázků uvnitř vydání. Výstup má jednotný formát `Horimiya - Ch 001.pdf`, Title i XMP název série jsou `Horimiya`. Desetinné indexy zůstávají zachované.

Živě ověřeno na Horimiyi: úplný seznam obsahuje 136 položek a po seřazení začíná Page. 1, Page. 2, Page. 3.

```powershell
.\.venv\Scripts\python.exe manga_leech.py "https://weebcentral.com/series/01J76XY7PBJ0A3GC5PC79VET42/Horimiya"
```

## Svazky Volume / Vol.

Parser podporuje `Volume 1`, `Volume. 1`, `Vol 1` a `Vol. 1` ve stránce série, RSS i přímé kapitole. Svazky se řadí číselně a ukládají jako `Název série - Vol 001.pdf` (desetinný index např. `Vol 002.5`). PDF Title a XMP série obsahují pouze název série. Kapitoly a položky Page dál používají `Ch`.

U série „The Guy She Was Interested In Wasn't a Guy at All“ bylo živě ověřeno načtení všech tří svazků z vloženého seznamu přímo na stránce, bez tlačítka Show All Chapters. Použít lze stejný příkaz jako pro ostatní série.

## Označení Episode

`Episode 1` i `Episode. 1` se zpracují jako kapitola, včetně desetinných čísel, RSS, přímého `--chapter`, metadat a řazení. Výstup používá `Ch`, například `your name - Ch 001.pdf`; název série v metadatech zůstává `your name.` dle webu. Živě ověřeno na Kimi No Na Wa: seznam obsahuje 9 položek a řadí se od Episode 1.

## Spotřeba paměti

Při tvorbě PDF se JPEG a kompatibilní PNG vkládají bez dekódování pixelů. Ostatní formáty se zpracují po jednom obrázku, včetně zachování průhlednosti. ReportLab stále drží komprimované streamy jednoho rozpracovaného PDF v paměti, takže velký svazek může vyžadovat více RAM než běžná kapitola.

PDF proces standardně zpracuje 10 kapitol a skončí; `--pdf-tasks-per-child 1` obnoví původní chování s restartem po každé kapitole. Po každém PDF se explicitně uvolní cyklické objekty. Pro nižší celkovou spotřebu ponechte `--pdf-workers 1`. Download vlákna zapisují síťová data po blocích přímo na disk, ale při prvním ověření potřebují dekódovat celý obrázek. Vyšší počet vláken proto může zvýšit špičku paměti během validace. Ověřená cache při dalším použití vyžaduje pouze průběžný výpočet SHA-256.

Kontrolní měření této revize na Windows (Python 3.12): devět opakování stejné 205stránkové JPEG kapitoly z cache trvalo 5,39 s před změnou a 2,52 s po změně, včetně startu a ukončení PDF procesů. Samotná kapitola zůstala přibližně 13,58 MB; medián tvorby včetně úklidu objektů byl 0,25 s. Nová kontrola obsahu a úklid přidávají malou režii jednotlivému PDF, hlavní zrychlení dávky přináší méně restartů. Nejde o měření rychlosti stahování.

Při 30 opakovaných renderováních a recyklaci po 10 úlohách byla paměť jednoho workeru po dokončení úloh přibližně 38–39 MiB, špička do 80 MiB. Jde o JPEG vzorek a PDF proces, nikoli maximum celé aplikace nebo záruku pro velké PNG a dlouhé webtoony.

Na kontrolním jednobitovém PNG kleslo PDF z 13 337 B na 6 105 B. U vzorku WebP klesla bezeztrátová varianta z 1 348 957 B na 1 037 085 B; s volitelným `--jpeg-quality 85` měla 155 951 B za cenu další ztrátové komprese. Poměry závisí na obsahu a zahrnují režii jednostránkového PDF.

Graf paměti kontejneru zahrnuje další procesy a podle způsobu měření také cache souborů. Samotný vysoký údaj tedy nedokazuje únik paměti downloaderu. Na Linuxu lze největší procesy a stav cache ověřit například:

```bash
ps -eo pid,ppid,comm,rss --sort=-rss | head -15
free -h
```

RSS z `ps` je v KiB; cache a údaj `available` z `free` pomohou odlišit využití paměti procesy od systémové cache.

## Označení Kapitel

`Kapitel 1` i `Kapitel. 1` se zpracují stejně jako Chapter: podporují načtení série i RSS, číselné řazení, metadata a výstup `Monster - Ch 001.pdf`. Rozpoznávání nerozlišuje velikost písmen.
