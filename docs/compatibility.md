# Game compatibility

The state of every game OpenShiina aims at. Update a game's row when something changes (see
[Updating](#updating) at the end).

| Status | Meaning |
|---|---|
| ✅ Plays through | Every route was played or skipped to its end |
| 🟢 Plays | Starts and its routes play, but not all of them were checked |
| 🟡 Starts | Boots to the title screen; the story was not tried yet |
| 🔴 Stops | Stops before the title screen |
| ⚪ Not tried | Not run yet |

## GRAND†CROSS "Plus" games

| Game | Japanese title | Release | VNDB | ShiinaRio | Status | Checked |
|---|---|---|---|---|---|---|
| Ero-On! | えろおん！ | 2010-04-29 | [v11473](https://vndb.org/v11473) | v2.49 | ✅ Plays through | 2026-10-07 |
| Azu Plus | アズプラス | 2010-08-15 | [v7579](https://vndb.org/v7579) | v2.47 | ✅ Plays through | 2026-10-07 |
| Oreimo Plus | 俺妹プラス | 2010-12-31 | [v6035](https://vndb.org/v6035) | v2.47 | ✅ Plays through | 2026-10-07 |
| Homu☆Plus | ほむ☆プラス | 2011-08-14 | [v8019](https://vndb.org/v8019) | v2.49 | ✅ Plays through | 2026-10-07 |
| Yuru Plus | ゆるプラス | 2011-11-25 | [v10125](https://vndb.org/v10125) | v2.49 | ✅ Plays through | 2026-10-07 |
| Sena Plus | 星奈プラス | 2011-12-31 | [v10126](https://vndb.org/v10126) | v2.49 | ✅ Plays through | 2026-10-07 |
| Kuroneko Plus | 黒猫プラス | 2012-05-18 | [v10586](https://vndb.org/v10586) | v2.49 | ✅ Plays through | 2026-10-07 |
| Nyaru Plus | ニャルプラス | 2012-08-12 | [v10779](https://vndb.org/v10779) | v2.49 | ✅ Plays through | 2026-10-07 |
| Rikka Plus | 六花プラス | 2012-12-31 | [v11902](https://vndb.org/v11902) | v2.49 | ✅ Plays through | 2026-10-07 |
| Maki Fes! | マキフェス！ | 2014-12-30 | [v16484](https://vndb.org/v16484) | v2.50 | ✅ Plays through | 2026-10-07 |
| Re:Rem Plus | Re:レムプラス | 2018-03-31 | [v22991](https://vndb.org/v22991) | v2.50 | ✅ Plays through | 2026-10-07 |

ShiinaRio is the engine version START.SCN checks (`03C0`, as in RIO.INI's section name). Games
of one version share most of their START.SCN; see
[engine-notes.md, section 9](engine-notes.md#9-the-other-games-survey-of-all-11-2026-10-03).

## Other ShiinaRio games

`Formats.Json` has the keys of 127 more ShiinaRio games (GARbro-Mod's scheme database, its
`KnownSchemes`). Their archives open (every WARC version, 1.0 to 1.7; 1.0 and 1.1 need no keys).
The interpreter knows engine versions 2.47, 2.49 and 2.50, and 2.34 in part (Ao no Juuai,
docs/todo.md "Other ShiinaRio games"); see engine-notes.md section 1, "The schemes" and "WARC
versions". Try a game with ScnBoot first: it says the first opcode it does not run.

"Scheme" is the version GARbro's scheme gives (its archives' keys). It is not always the engine
version START.SCN checks: Homu☆Plus's scheme says v2.50, its engine is v2.49. Executables are
those GameMap names for the game (how the players tell it).

| Game | Japanese title | Scheme | Executable | Status |
|---|---|---|---|---|
| Hana no Kioku 1-2-3 | 花の記憶 わん・つう・すり～ | v2.10 | `HK123.EXE` | ⚪ Not tried |
| Shi-Ka-E-Shi | シ・カ・エ・シ | v2.15 | `FKS.EXE` | ⚪ Not tried |
| Hana no Kioku 7 | 花の記憶 ～第七章～ | v2.18 | `HK7.EXE` | ⚪ Not tried |
| Hana no Kioku 4-5-6 | 花の記憶 よん・ごー・ろく | v2.19 | `HK456.EXE` | ⚪ Not tried |
| STAGE |  | v2.20 | — | ⚪ Not tried |
| Chain Trap ~Fukushuu no Midara Wana~ | CHAIN TRAP ～復讐の淫罠～ | v2.30 | `CTRAP.EXE` | ⚪ Not tried |
| Kateinai Choukyou | 家庭内調教 | v2.31 | `katei.exe` | ⚪ Not tried |
| Cleavage |  | v2.33 | — | ⚪ Not tried |
| Death☆Meta |  | v2.33 | — | ⚪ Not tried |
| Vanquish |  | v2.33 | `VANQ.EXE` | ⚪ Not tried |
| Ao no Juuai | 青の獣愛 | v2.34 | `aoj.EXE` | 🟡 Starts (ScnBoot: the story to frame 30,000, choices; not tried in the players yet) |
| ShiinaRio v2.34 and older |  | v2.34 | — | ⚪ Not tried |
| ONE☆BOKU Faraway so close! |  | v2.35 | — | ⚪ Not tried |
| CC Hospital | CCホスピタル | v2.36 | `CCH.EXE` | ⚪ Not tried |
| Gibo to Oba ~Soshite Yuujin no Haha~ | 義母と叔母 ～そして友人の母～ | v2.36 | `MAMA.EXE` | ⚪ Not tried |
| Hitozuma OL -Chijoku no Gyoumu Meirei- | 人妻OL -恥辱の業務命令- | v2.36 | `HDOL.EXE` | ⚪ Not tried |
| Sabae no Ou | 蠅声の王 | v2.36 | — | ⚪ Not tried |
| ShiinaRio v2.36-2.38 | 椎名里緒 v2.36 | v2.36 | — | ⚪ Not tried |
| Shinseki no Oba-san | 親戚の小母さん ～離れの熟女、本家の後妻～ | v2.36 | — | ⚪ Not tried |
| Wana ~Hakudaku Mamire no Houkago~ | 輪罠 ～白濁まみれの放課後～ | v2.36 | — | ⚪ Not tried |
| Classmate no Okaa-san | クラスメイトのお母さん | v2.37 | `CLASS.exe` | ⚪ Not tried |
| Houkago no Hoken'i ~Ane Juujoku~ | 放課後の保健医 ～姉・汁辱～ | v2.37 | `hou_nc.EXE` | ⚪ Not tried |
| Jukubo Gui ~Moto Guradoru no Okaa-san~ | 熟母喰い ～元グラドルのお母さん～ | v2.37 | `JUKUBO.EXE` | ⚪ Not tried |
| Omocha no Sensei Nawa Midori | おもちゃの先生 那波みどり | v2.37 | `RK01_D.WAR`, `RK01_S.WAR` | ⚪ Not tried |
| Otome Juurin Yuugi | 乙女蹂躙遊戯～Maiden Infringement Play～ | v2.37 | — | ⚪ Not tried |
| Tsukutori | つくとり | v2.37 | — | ⚪ Not tried |
| Yakata Jukujo ~The Immoral Residence~ | 館熟女 ～The immoral residence～ | v2.37 | — | ⚪ Not tried |
| Draculius | ドラクリウス | v2.38 | — | ⚪ Not tried |
| Metro Chikansen  ~Kairaku Jousha Annai~ | 牝トロ痴漢線 ～快楽嬢射案内～ | v2.38 | — | ⚪ Not tried |
| Tentacle Busters | テンタクル☆バスターズ 対未確認生命体交渉係り | v2.38 | `TENTACLE.EXE` | ⚪ Not tried |
| Hitozuma Onna Kyoushi Reika | 人妻女教師・麗香 | v2.39 | `HITODUMA.EXE` | ⚪ Not tried |
| Nagagutsu wo Haita Deco | 長靴をはいたデコ | v2.39 | — | ⚪ Not tried |
| Helter Skelter | ヘルタースケルター | v2.40 | `HELTER.exe` | ⚪ Not tried |
| Otome Chibaku Yuugi | 乙女恥曝遊戯～Disgrace Return Play～ | v2.40 | — | ⚪ Not tried |
| Reizoku ~Kyonyuu Shimai Choukyou Jugyou~ | 隷属 ～巨乳姉妹調教授業～ | v2.40 | `RZK.exe` | ⚪ Not tried |
| Tsui☆Teru | ツイ☆てる | v2.40 | `TUITE.EXE` | ⚪ Not tried |
| Kichiku Nakadashi Suieibu | 鬼畜中出し水泳部～キャプテン氷川美玲・汁辱～ | v2.41 | — | ⚪ Not tried |
| Zansho Omimai Moushiagemasu | 残暑お見舞い申し上げます。 | v2.41 | — | ⚪ Not tried |
| Otometeki Koi Kakumei★Love Revo!! | 乙女的恋革命★ラブレボ!! | v2.42 | `LoveRevo.EXE` | ⚪ Not tried |
| Chikan Densha Otoko 2 | 痴漢電車男2 ～伝説へのライナー～ | v2.43 | `TRAIN2.exe` | ⚪ Not tried |
| Dokodemo Sukishite Itsudemo Sukishite | どこでもすきしていつでもすきして | v2.44 | `SITESITE.exe` | ⚪ Not tried |
| Wana II ~Gang Rape~ | 輪罠II ～Gang Rape～ | v2.44 | `WANA_2.exe` | ⚪ Not tried |
| Yokorenbo ~Immoral Mother~ | 横恋母 ～Immoral Mother～ | v2.44 | `YOKOREN.EXE` | ⚪ Not tried |
| Chuuchuu Nurse | ちゅうちゅうナース | v2.45 | — | ⚪ Not tried |
| Engage Links |  | v2.45 | `ENGAGE.exe` | ⚪ Not tried |
| Hitozuma Gui ~Manbiki G-man Chijoku Nikki~ | 人妻喰い ～万引きGメン恥辱日記～ | v2.45 | — | ⚪ Not tried |
| Manin Chijo Densha 2 | 満淫痴女電車2～姉とふたごと人妻と～ | v2.45 | `MCD2.exe` | ⚪ Not tried |
| Niizuma to Yuukaihan | 新妻と誘拐犯 ～寝取り孕ませ計画～ | v2.45 | — | ⚪ Not tried |
| Tsuyoimo x Yowaimo | 強妹×弱妹 | v2.45 | `IMOIMO.exe` | ⚪ Not tried |
| Chikan Circle | 痴漢サークル | v2.46 | `tikan1.exe`, `tikan1~.exe` | ⚪ Not tried |
| Douryou no Oku-san ~Netori Tsuma, Netorare Tsuma~ | 同僚の奥さん～ネトリ妻、ネトラレ妻～ | v2.46 | — | ⚪ Not tried |
| Hana to Otome ni Shukufuku o | 花と乙女に祝福を | v2.46 | `HANAOTO.exe` | ⚪ Not tried |
| Idol Koukai Chijoku Sex | アイドル公開恥辱SEX | v2.46 | `ISEX.exe` | ⚪ Not tried |
| Mikoko | みここ | v2.46 | `MIKOKO.exe` | ⚪ Not tried |
| Shokuinshitsu | 職員室 | v2.46 | — | ⚪ Not tried |
| Stellula Eques Codex | ステルラエクエス コーデックス ～黄昏の姫騎士～ | v2.46 | `STCODE.exe` | ⚪ Not tried |
| Can Fes! ~Itazura Majo to Naisho no Gakuensai~ | きゃん☆フェス！～いたずら魔女とナイショの学園祭～ | v2.47 | — | ⚪ Not tried |
| Chikan Circle 2 | 痴漢サークル2 | v2.47 | `CHIKAN2.exe`, `CHIKAN2_.exe` | ⚪ Not tried |
| Chikan Circle 3 | 痴漢サークル3 | v2.47 | `CHIKAN3.exe`, `CHIKAN3_.exe` | ⚪ Not tried |
| Chikan Densha Otoko Gaiden | 痴漢電車男外伝 ～伝説へのチェイサー～ | v2.47 | `TRAING.EXE` | ⚪ Not tried |
| Damatte Watashi no Muko ni Nare! | 黙って私のムコになれ！ | v2.47 | — | ⚪ Not tried |
| Danchi Wife ~Hitozuma Nude Model no Kenshin~ | 団地ワイフ～人妻ヌードモデルの献身～ | v2.47 | `DANCHIDL.exe`, `DANCHIDL_.exe` | ⚪ Not tried |
| Hana to Otome ni Shukufuku o Royal Bouquet | 花と乙女に祝福を ロイヤルブーケ | v2.47 | — | ⚪ Not tried |
| Iyashinbo The Motion | 癒しん母 The Motion ～世界で一番好きなひと～ | v2.47 | `IYASHIm.EXE` | ⚪ Not tried |
| Last Waltz ~Hakudaku Mamire no Natsu Gasshuku~ | Last Waltz ～白濁まみれの夏合宿～ | v2.47 | `WALTZ.exe` | ⚪ Not tried |
| Mahou Shoujo no Taisetsu na Koto | 魔法少女の大切なこと。 | v2.47 | `MAHOST.exe` | ⚪ Not tried |
| Mimi o Sumaseba | みみをすませば | v2.47 | `mimisuma.exe` | ⚪ Not tried |
| Najimi no Oba-chan | 馴染みのオバちゃん | v2.47 | — | ⚪ Not tried |
| Nakadashi Trilogy | なかだしトリロジー | v2.47 | — | ⚪ Not tried |
| Niwaka Ane | にわか姉 | v2.47 | `NIWAKA.exe` | ⚪ Not tried |
| Onedari Onapet | おねだりオナペット | v2.47 | — | ⚪ Not tried |
| Oshikake! Entremets | おしかけ！アントルメ | v2.47 | `OSHIAN.exe` | ⚪ Not tried |
| Pure Love! | ぴゅあらっ！ | v2.47 | — | ⚪ Not tried |
| Ran→Sem | RAN→SEM～白濁デルモ妻のミイラ捕り～ | v2.47 | `RANDL.exe`, `RANDL_.exe` | ⚪ Not tried |
| Ren'ai Saimin ~Tsun na Kanojo ga dereru Saimin~ | 恋愛催眠～ツンな彼女がデレる催眠～ | v2.47 | `RENSAI.exe` | ⚪ Not tried |
| Rin x Sen | RIN×SEN～白濁女教師と野郎ども～ | v2.47 | — | ⚪ Not tried |
| Ryoumaden ~Houkago no Rakuen~ | 凌魔伝 ～放課後の楽園～ | v2.47 | `RYOMADEN.exe` | ⚪ Not tried |
| Saimin Seikatsu | 催眠生活 ～校則だから仕方ない！？～ | v2.47 | `SAIMIN.exe` | ⚪ Not tried |
| Saint ~Aa Shu yo | セイント ～あぁ主よ、教え子達に堕とされた私をお許し下さい～ | v2.47 | `SAINT.exe` | ⚪ Not tried |
| Tenki Ane | 天気姉 | v2.47 | `TENKIANE.exe` | ⚪ Not tried |
| Vestige -Yaiba ni Nokoru wa Kimi no Omokage- | Vestige ―刃に残るは君の面影― | v2.47 | `VESTIGE.exe` | ⚪ Not tried |
| You~Gaku | よう∽ガク | v2.47 | — | ⚪ Not tried |
| Yume Miru Egoist | 夢みるエゴイスト | v2.47 | `EGOIST.exe` | ⚪ Not tried |
| Bloody Rondo | BLOODY†RONDO | v2.48 | — | ⚪ Not tried |
| Gakushoku no Oba-san The Motion | 学食のおばさん The Motion ～母さんの汁の味～ | v2.48 | `GAKUOBAm.exe` | ⚪ Not tried |
| Onegan! | おねガン！ | v2.48 | `ONEGAN.exe` | ⚪ Not tried |
| Sensei! Shite Ageru | 先生っ！ シてあげる | v2.48 | `SHITEAGE.exe` | ⚪ Not tried |
| Tanetsuke Mura | 種憑け村 ～白濁神、念仏講ノ儀～ | v2.48 | `tane.exe` | ⚪ Not tried |
| Aneiro | アネイロ | v2.49 | `ANEIRO.exe` | ✅ Plays through (ver 1.03a; skipped to the end by the user, 2026-10-10) |
| Boku Igai no Otoko o Shiranai Kanojo | 僕以外の男を知らない彼女が他の男に抱かれていた | v2.49 | `MATNTR.exe` | ⚪ Not tried |
| Chou Saiminjutsu Gakuen | 超催眠術学園 | v2.49 | — | ⚪ Not tried |
| Doushite Daite Kurenai no!? | どうして抱いてくれないのっ!?～女の子だってヤりたいの！～ | v2.49 | `DODAKURE.exe` | ⚪ Not tried |
| Gensou no Idea ~Oratorio Phantasm Historia~ | 幻創のイデア～Oratorio Phantasm Historia～ | v2.49 | — | ⚪ Not tried |
| Hin wa Bokura no Fuku no Kami | 貧は僕らの福の神 | v2.49 | `BINBO.EXE` | ⚪ Not tried |
| Intruder | イントルーダー ～侵入者～ | v2.49 | `INTRUDER.exe` | ⚪ Not tried |
| Majime to Sasayakareru Ore o Osananajimi no Risa | 真面目と囁かれるオレを幼なじみの理彩が性的な意味も込めて陥落していく話 | v2.49 | `MAJIKAN01.exe` | ⚪ Not tried |
| Puchipuchi Idol Kouhosei | ぷちぷちアイドル候補生 ～早くシてよ！マネージャーでしょ～ | v2.49 | `PETITxPETIT.exe` | ⚪ Not tried |
| Shinigami no Testament | 死神のテスタメント ～menuet of epistula～ | v2.49 | `TESTAMENT.exe` | ⚪ Not tried |
| Shojo Mama | 処女ママ | v2.49 | `SHOJOMAMA.EXE` | ⚪ Not tried |
| Son of a ☆ Bitch | サノバ☆ビッチ ～早乙女野薔薇ちゃん、マジ天使!～ | v2.49 | — | ⚪ Not tried |
| Tojita Sekai no Tori Colony | 閉じたセカイのトリコロニー | v2.49 | — | ⚪ Not tried |
| Toriko no Chigiri | 虜ノ契 ～家族のために身体を差し出す姉と妹～ | v2.49 | `TORIGIRI.exe` | ⚪ Not tried |
| Zettai Zetsumei Shoujo | 絶体絶命少女 | v2.49 | `ZZS.exe` | ⚪ Not tried |
| Bitch Gakuen ga Seijun na Hazu ga Nai!!? | ビッチ学園が清純なはずがないっ！！？ | v2.50 | `BITCHES3.exe` | ⚪ Not tried |
| Bitch Nee-chan ga Seijun na Hazu ga Nai! | ビッチ姉ちゃんが清純なはずがないっ！ | v2.50 | `BITCHES.EXE` | 🟡 Starts (ver 1.02; ScnBoot: title, the story to frame 30,000; to test more in the players) |
| Bitch Shimai ga Seijun na Hazu ga Nai!! | ビッチ姉妹が清純なはずがないっ！！ | v2.50 | `BITCHES2.exe` | ⚪ Not tried |
| Chiccha na Hanayome ~Mada Mada Tsubomi da mon~ | ちっちゃな花嫁 ～まだまだつぼみだもんっ～ | v2.50 | `CHIPPANA.exe` | ⚪ Not tried |
| Chuuni Hime no Teikoku | 厨二姫の帝国 | v2.50 | `CHU2HIME.exe` | ⚪ Not tried |
| Gohoushi Nurse ~Mayonaka no Kyousei Call~ | ご奉仕ナース ～真夜中の強精コール～ | v2.50 | `GHNURSE.exe` | ⚪ Not tried |
| Gyaku Katei Kyoushi | 逆家庭教師 ～彼女は僕の先生にして奴隷～ | v2.50 | `GYAKUKATE.exe` | ⚪ Not tried |
| Kanojo ga Ore ni Kureta Mono | 彼女が俺にくれたもの。俺が彼女にあげるもの。 | v2.50 | `MONOMONO.exe` | ⚪ Not tried |
| Kiki Mimi | ききミミ | v2.50 | `KIKIMIMI.exe` | ⚪ Not tried |
| Kinpatsu Tarou Monogatari | 金髪太郎物語 | v2.50 | `KINPATSU.exe` | ⚪ Not tried |
| Kyuuketsuki no Libra | 吸血姫のリブラ | v2.50 | `LIBRA.exe` | ⚪ Not tried |
| Libra of the Vampire Princess |  | v2.50 | `LIBRAeS.exe` | ⚪ Not tried |
| Lovecha ~Tsumasakidachi de, Hitomi o Tojite~ | らぶちゃ ～つま先立ちで、瞳をとじて～ | v2.50 | — | ⚪ Not tried |
| Nukige Mitai na Shima ni Sunderu | 抜きゲーみたいな島に住んでる貧乳はどうすりゃいいですか? | v2.50 | `NUKITASHI.exe` | ⚪ Not tried |
| Nukige Mitai na Shima ni Sunderu [Trial] | 抜きゲーみたいな島に住んでる貧乳はどうすりゃいいですか? 体験版 | v2.50 | `NUKITASHI_TRIAL.exe` | ⚪ Not tried |
| Nukige Mitai na Shima ni Sunderu... 2 | 抜きゲーみたいな島に住んでる貧乳はどうすりゃいいですか? 2 | v2.50 | — | ⚪ Not tried |
| Oni ga Kuru | 鬼がくる。～姉がひん死でピンチです～ | v2.50 | `ONIKURU.exe` | ⚪ Not tried |
| Raillore no Ryakudatsusha | レイルロアの略奪者 | v2.50 | — | ⚪ Not tried |
| Saimin Paradise! | 催眠ぱらだいす! ～催眠術でツゴウノイイ美少女学園性活～ | v2.50 | `SAIPARA.exe` | ⚪ Not tried |
| Shuukatsu Katei Kyoushi | シュウカツ家庭教師 | v2.50 | — | ⚪ Not tried |
| Signalist Stars!! | しぐなリストスタ～ズ!! | v2.50 | `SIGSTA.exe`, `SIGSTA_ZERO.exe` | ⚪ Not tried |
| Sorcery Jokers | ソーサリージョーカーズ | v2.50 | `SJS.exe` | ⚪ Not tried |
| Sorcery Jokers [English] |  | v2.50 | `SJSE.exe` | ⚪ Not tried |
| Tsugou no Ii Idol | ツゴウノイイアイドル | v2.50 | — | ⚪ Not tried |

## Platforms

| Platform | Player | Status | Checked |
|---|---|---|---|
| Windows 10 / 11 | WPF (`OpenShiina.Windows`) and Avalonia (`OpenShiina.Desktop`) | ✅ The games above | 2026-10-08 |
| Android | Avalonia (`OpenShiina.Android`), Release built with LLVM | 🟢 Plays | 2026-10-08 |
| Linux, macOS | Avalonia (`OpenShiina.Desktop`) | ⚪ Builds; no game run recorded here | |
| iOS | not written yet | ⚪ Planned | |

**Windows** The games were played and checked on Windows 10 (6 cores, RTX 2070, a 240 Hz
screen). At the game's own pace a title screen costs about as much processor as the original
exe (Re:Rem Plus's: about 3% of the machine for both). The GPU mode needs a Vulkan driver.

**Android** Checked on a Galaxy S7 (Exynos 8890, Mali-T880, Vulkan 1.0) with Maki Fes!: it
plays, 18.4 frames a second over a session from its perf.log (6.2 before the Release build used
LLVM and the changes of 2026-10-08). Zooms and transitions still drop frames on a phone that old.
Touch controls are described in the README.

## Notes

**Ero-On!** Every route skipped through. It needed `079F` (the window loses its maximise box)
and C# versions of its own zoom routines (START 5E2D6 scaling down, 5DFA5 enlarging: an earlier
build than the other games', engine-notes.md section 10). It is shorter than the others, its
scenario commands are a subset of theirs (34, PRELOAD numbered differently), and the game itself
has no saves: its title offers only start and quit.

**Azu Plus** Needed `03C2` (a checksum of the executable, checked at start), `05C1` and `0516`
(movies drawn straight onto the window). Every route played through.

**Oreimo Plus** The first game. Every route skipped through three times; title, choices, saves,
OPTION page, backlog, movies (MovieMode 0 / 1 and 2).

**Homu☆Plus** Every route skipped through; nothing was added for it.

**Yuru Plus** Every route skipped through; nothing was added for it.

**Sena Plus** Every route skipped through. It was slow (frames over 100 ms) until the v2.49
builds of the hot embedded routines got their C# versions (engine-notes.md, section 10).

**Kuroneko Plus** Every route skipped through; nothing was added for it.

**Nyaru Plus** Every route skipped through; nothing was added for it.

**Rikka Plus** Every route skipped through. It is the only v2.49 game using `0548` (a surface
of a given size), in START's function 207, which plays a movie on its own with MovieMode 0 / 1
and is reached from TOPMENU's `mv\STCODE_T.MPG` and the `MOVIE` command; the scenario never
uses `MOVIE`, so play never reaches it. `0548` was added afterwards (Maki Fes! and Re:Rem Plus
make their pages with it).

**Maki Fes!** Every route skipped through. Its ending movie is a Windows Media file
(`mv\ed.wmv`), played as the MPEG-1 `mv\ed.mpg` the game ships beside it.

**Re:Rem Plus** Every route skipped through. Its zoomed scenes (`$A_CHR` 40 / 41, a 1600 x 900
picture scaled to the screen every frame) took 200 ms a frame until its scaling routine ran as
C# (a third build of scale32; engine-notes.md, section 10).

**Both (engine v2.50)** 1280 x 720, 53 scenario commands, shown through Direct3D. v2.50 has an
opcode table of its own (read from REMPLUS.EXE: Data/ScnOps/ops_v250.tsv) and 1025 picture
slots; they needed the v2.49 opcodes their START uses (ScnVm.Menus.cs) and four of v2.50
(ScnVm.Engine250.cs: zlib-packed blocks for the saves, characters drawn from the scripts' own
pictures); see engine-notes.md, section 10. The window menu the game puts on its window (exit,
window size) is not shown: the players' own window does both.

## Updating

When a game's state changes:

1. Set its **Status** to the row of the legend that fits and **Checked** to the date (YYYY-MM-DD).
2. Under **Notes**, say what was checked and how (played, skipped with Ctrl, ScnBoot), what was
   added for it, and what is known not to work.
3. When a platform was tried (a phone, Linux, macOS), add or update its row under **Platforms**
   with the device and what ran.
4. If it is the headline of a change, the commit message can name the game; README links here
   and does not list the games itself.

To see which opcodes a game's scripts use that OpenShiina does not run yet, boot it in ScnBoot
(`tests/OpenShiina.ScnBoot`): it stops at the first one and prints it.
