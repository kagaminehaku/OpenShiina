# Game compatibility

The state of every ShiinaRio game OpenShiina knows the keys of (`Formats.Json`). Update a game's
row when something changes (see [Updating](#updating) at the end).

| Status | Meaning |
|---|---|
| ✅ Plays through | Every route was played or skipped to its end |
| 🟢 Plays | Starts and its routes play, but not all of them were checked |
| 🟡 Starts | Boots to the title screen; the story was not tried yet |
| 🔴 Stops | Stops before the title screen |
| ⚪ Not tried | Not run yet |

## Games

Every game whose archives OpenShiina can open: the eleven GRAND†CROSS "Plus" games it started
with, and the other ShiinaRio games of GARbro-Mod's scheme database. Their archives open, every
WARC version (1.0 to 1.7; 1.0 and 1.1 need no keys); whether a game plays depends on its engine
version. The interpreter runs engine versions 2.45, 2.47, 2.49 and 2.50, and 2.34 in part; see
[engine-notes.md](engine-notes.md), section 1 ("The schemes", "WARC versions") and section 10.
Try a game with `ScnTools opcheck <game folder>` first: the opcodes and DLL functions of all its
scripts the player does not run yet (none in the 17 games tried). Then ScnBoot
(`tests/OpenShiina.ScnBoot`): it plays the game without a screen and says where it stops.

- **Release**: the game's first release, or the release the scheme is for (a compilation, a
  "The Motion" edition, a trial, an English version).
- **VNDB**: the game's page, or that release's (`r...`).
- **ShiinaRio**: the engine version START.SCN checks (`03C0`, as RIO.INI's section names it) for
  the games tried; for the others, the version their scheme in GARbro gives, which is not always
  the engine's (Homu☆Plus's scheme says v2.50, its engine is v2.49). Games of one version share
  most of their START.SCN.

The executables each game is recognised by are in `Formats.Json` (`GameMap`). It also has two
schemes that are not one game: "ShiinaRio v2.34 and older" and "ShiinaRio v2.36-2.38", for games
of those versions it does not list.

| Game | Japanese title | Release | VNDB | ShiinaRio | Status | Checked |
|---|---|---|---|---|---|---|
| Hana no Kioku 1-2-3 | 花の記憶 わん・つう・すり～ | 2000-11-24 | [r19761](https://vndb.org/r19761) | v2.10 | ⚪ Not tried |  |
| Shi-Ka-E-Shi | シ・カ・エ・シ | 2002-01-25 | [v5574](https://vndb.org/v5574) | v2.15 | ⚪ Not tried |  |
| Hana no Kioku 7 | 花の記憶 ～第七章～ | 2002-12-20 | [v8713](https://vndb.org/v8713) | v2.18 | ⚪ Not tried |  |
| Hana no Kioku 4-5-6 | 花の記憶 よん・ごー・ろく | 2003-02-14 | [r19762](https://vndb.org/r19762) | v2.19 | ⚪ Not tried |  |
| STAGE | STAGE | 2003-05-02 | [v7271](https://vndb.org/v7271) | v2.20 | ⚪ Not tried |  |
| Kateinai Choukyou | 家庭内調教 | 2004-01-23 | [v4330](https://vndb.org/v4330) | v2.31 | ⚪ Not tried |  |
| Chain Trap ~Fukushuu no Midara Wana~ | CHAIN TRAP ～復讐の淫罠～ | 2004-04-02 | [v9632](https://vndb.org/v9632) | v2.30 | ⚪ Not tried |  |
| Vanquish | VANQUISH | 2004-10-22 | [v7459](https://vndb.org/v7459) | v2.33 | ⚪ Not tried |  |
| Death☆Meta | です☆めた | 2004-11-26 | [v5707](https://vndb.org/v5707) | v2.33 | ⚪ Not tried |  |
| Hitozuma Onna Kyoushi Reika | 人妻女教師・麗香 | 2004-12-10 | [v3719](https://vndb.org/v3719) | v2.39 | ⚪ Not tried |  |
| Cleavage | CLEAVAGE | 2005-02-18 | [v931](https://vndb.org/v931) | v2.33 | ⚪ Not tried |  |
| Ao no Juuai | 青の獣愛 | 2005-06-24 | [v4048](https://vndb.org/v4048) | v2.34 | 🟡 Starts | 2026-10-11 |
| ONE☆BOKU Faraway so close! | ONE☆BOKU Faraway so close！～お姉ちゃんとボク～ | 2005-08-26 | [v2067](https://vndb.org/v2067) | v2.35 | ⚪ Not tried |  |
| Wana ~Hakudaku Mamire no Houkago~ | 輪罠 ～白濁まみれの放課後～ | 2005-10-21 | [v6680](https://vndb.org/v6680) | v2.36 | ⚪ Not tried |  |
| Gibo to Oba ~Soshite Yuujin no Haha~ | 義母と叔母 ～そして友人の母～ | 2005-12-22 | [v2498](https://vndb.org/v2498) | v2.36 | ⚪ Not tried |  |
| Hitozuma OL -Chijoku no Gyoumu Meirei- | 人妻OL -恥辱の業務命令- | 2005-12-22 | [v2911](https://vndb.org/v2911) | v2.36 | ⚪ Not tried |  |
| Otometeki Koi Kakumei★Love Revo!! | 乙女的恋革命★ラブレボ!! | 2006-01-26 | [v3549](https://vndb.org/v3549) | v2.42 | ⚪ Not tried |  |
| CC Hospital | CCホスピタル | 2006-03-24 | [v5300](https://vndb.org/v5300) | v2.36 | ⚪ Not tried |  |
| Omocha no Sensei Nawa Midori | おもちゃの先生 那波みどり | 2006-03-31 | [v6632](https://vndb.org/v6632) | v2.37 | ⚪ Not tried |  |
| Shinseki no Oba-san | 親戚の小母さん ～離れの熟女、本家の後妻～ | 2006-03-31 | [v4399](https://vndb.org/v4399) | v2.36 | ⚪ Not tried |  |
| Sabae no Ou | 蠅声の王 | 2006-04-28 | [v607](https://vndb.org/v607) | v2.36 | ⚪ Not tried |  |
| Classmate no Okaa-san | クラスメイトのお母さん | 2006-08-04 | [v5862](https://vndb.org/v5862) | v2.37 | ⚪ Not tried |  |
| Otome Juurin Yuugi | 乙女蹂躙遊戯～Maiden Infringement Play～ | 2006-12-22 | [v4352](https://vndb.org/v4352) | v2.37 | ⚪ Not tried |  |
| Jukubo Gui ~Moto Guradoru no Okaa-san~ | 熟母喰い ～元グラドルのお母さん～ | 2007-01-19 | [v3358](https://vndb.org/v3358) | v2.37 | ⚪ Not tried |  |
| Yakata Jukujo ~The Immoral Residence~ | 館熟女 ～The immoral residence～ | 2007-03-23 | [v6109](https://vndb.org/v6109) | v2.37 | ⚪ Not tried |  |
| Houkago no Hoken'i ~Ane Juujoku~ | 放課後の保健医 ～姉・汁辱～ | 2007-03-30 | [v3043](https://vndb.org/v3043) | v2.37 | ⚪ Not tried |  |
| Tentacle Busters | テンタクル☆バスターズ 対未確認生命体交渉係り | 2007-04-27 | [v3795](https://vndb.org/v3795) | v2.38 | ⚪ Not tried |  |
| Draculius | ドラクリウス | 2007-05-25 | [v453](https://vndb.org/v453) | v2.38 | ⚪ Not tried |  |
| Tsukutori | つくとり | 2007-05-25 | [v637](https://vndb.org/v637) | v2.37 | ⚪ Not tried |  |
| Metro Chikansen  ~Kairaku Jousha Annai~ | 牝トロ痴漢線 ～快楽嬢射案内～ | 2007-07-06 | [v5266](https://vndb.org/v5266) | v2.38 | ⚪ Not tried |  |
| Nagagutsu wo Haita Deco | 長靴をはいたデコ | 2007-09-28 | [v606](https://vndb.org/v606) | v2.39 | ⚪ Not tried |  |
| Tsui☆Teru | ツイ☆てる | 2007-11-22 | [v454](https://vndb.org/v454) | v2.40 | ⚪ Not tried |  |
| Helter Skelter | ヘルタースケルター | 2007-11-30 | [v2784](https://vndb.org/v2784) | v2.40 | ⚪ Not tried |  |
| Otome Chibaku Yuugi | 乙女恥曝遊戯～Disgrace Return Play～ | 2007-12-21 | [v4154](https://vndb.org/v4154) | v2.40 | ⚪ Not tried |  |
| Zansho Omimai Moushiagemasu | 残暑お見舞い申し上げます。 | 2008-02-29 | [v625](https://vndb.org/v625) | v2.41 | ⚪ Not tried |  |
| Kichiku Nakadashi Suieibu | 鬼畜中出し水泳部～キャプテン氷川美玲・汁辱～ | 2008-03-14 | [v3968](https://vndb.org/v3968) | v2.41 | ⚪ Not tried |  |
| Chikan Densha Otoko 2 | 痴漢電車男2 ～伝説へのライナー～ | 2008-05-30 | [v5861](https://vndb.org/v5861) | v2.43 | ⚪ Not tried |  |
| Dokodemo Sukishite Itsudemo Sukishite | どこでもすきしていつでもすきして | 2008-06-27 | [v780](https://vndb.org/v780) | v2.44 | ⚪ Not tried |  |
| Wana II ~Gang Rape~ | 輪罠II ～Gang Rape～ | 2008-06-27 | [v4371](https://vndb.org/v4371) | v2.44 | ⚪ Not tried |  |
| Yokorenbo ~Immoral Mother~ | 横恋母 ～Immoral Mother～ | 2008-07-18 | [v2970](https://vndb.org/v2970) | v2.44 | ⚪ Not tried |  |
| Engage Links | Engage Links | 2008-10-24 | [v1090](https://vndb.org/v1090) | v2.45 | ⚪ Not tried |  |
| Tsuyoimo x Yowaimo | 強妹×弱妹 | 2008-10-24 | [v2185](https://vndb.org/v2185) | v2.45 | ⚪ Not tried |  |
| Chuuchuu Nurse | ちゅうちゅうナース | 2008-10-31 | [v1144](https://vndb.org/v1144) | v2.45 | 🟡 Starts | 2026-10-11 |
| Manin Chijo Densha 2 | 満淫痴女電車2～姉とふたごと人妻と～ | 2008-11-28 | [v5511](https://vndb.org/v5511) | v2.45 | ⚪ Not tried |  |
| Hitozuma Gui ~Manbiki G-man Chijoku Nikki~ | 人妻喰い ～万引きGメン恥辱日記～ | 2008-12-16 | [v2897](https://vndb.org/v2897) | v2.45 | ⚪ Not tried |  |
| Niizuma to Yuukaihan | 新妻と誘拐犯 ～寝取り孕ませ計画～ | 2008-12-19 | [v6456](https://vndb.org/v6456) | v2.45 | ⚪ Not tried |  |
| Reizoku ~Kyonyuu Shimai Choukyou Jugyou~ | 隷属 ～巨乳姉妹調教授業～ | 2009-01-30 | [r13972](https://vndb.org/r13972) | v2.40 | ⚪ Not tried |  |
| Idol Koukai Chijoku Sex | アイドル公開恥辱SEX | 2009-02-13 | [v3063](https://vndb.org/v3063) | v2.46 | ⚪ Not tried |  |
| Mikoko | みここ | 2009-02-27 | [v1363](https://vndb.org/v1363) | v2.46 | ⚪ Not tried |  |
| Douryou no Oku-san ~Netori Tsuma, Netorare Tsuma~ | 同僚の奥さん～ネトリ妻、ネトラレ妻～ | 2009-03-06 | [v2280](https://vndb.org/v2280) | v2.46 | ⚪ Not tried |  |
| Chikan Circle | 痴漢サークル | 2009-05-01 | [v1703](https://vndb.org/v1703) | v2.46 | ⚪ Not tried |  |
| Hana to Otome ni Shukufuku o | 花と乙女に祝福を | 2009-05-29 | [v1545](https://vndb.org/v1545) | v2.46 | ⚪ Not tried |  |
| Stellula Eques Codex | ステルラエクエス コーデックス ～黄昏の姫騎士～ | 2009-05-29 | [v1651](https://vndb.org/v1651) | v2.46 | ⚪ Not tried |  |
| Shokuinshitsu | 職員室 | 2009-06-26 | [v1696](https://vndb.org/v1696) | v2.46 | ⚪ Not tried |  |
| Chikan Circle 2 | 痴漢サークル2 | 2009-07-03 | [v2102](https://vndb.org/v2102) | v2.47 | ⚪ Not tried |  |
| Chikan Densha Otoko Gaiden | 痴漢電車男外伝 ～伝説へのチェイサー～ | 2009-07-10 | [v1997](https://vndb.org/v1997) | v2.47 | ⚪ Not tried |  |
| Onedari Onapet | おねだりオナペット | 2009-07-31 | [v2213](https://vndb.org/v2213) | v2.47 | ⚪ Not tried |  |
| Chikan Circle 3 | 痴漢サークル3 | 2009-08-21 | [v2424](https://vndb.org/v2424) | v2.47 | ⚪ Not tried |  |
| Mimi o Sumaseba | みみをすませば | 2009-08-28 | [v2208](https://vndb.org/v2208) | v2.47 | ⚪ Not tried |  |
| Mahou Shoujo no Taisetsu na Koto | 魔法少女の大切なこと。 | 2009-09-25 | [v2616](https://vndb.org/v2616) | v2.47 | ⚪ Not tried |  |
| Rin x Sen | RIN×SEN～白濁女教師と野郎ども～ | 2009-09-25 | [v2611](https://vndb.org/v2611) | v2.47 | ⚪ Not tried |  |
| Nakadashi Trilogy | なかだしトリロジー | 2009-10-30 | [r5241](https://vndb.org/r5241) | v2.47 | ⚪ Not tried |  |
| Last Waltz ~Hakudaku Mamire no Natsu Gasshuku~ | Last Waltz ～白濁まみれの夏合宿～ | 2009-11-27 | [v2864](https://vndb.org/v2864) | v2.47 | ⚪ Not tried |  |
| Niwaka Ane | にわか姉 | 2009-12-11 | [v3005](https://vndb.org/v3005) | v2.47 | ⚪ Not tried |  |
| Hana to Otome ni Shukufuku o Royal Bouquet | 花と乙女に祝福を ロイヤルブーケ | 2010-01-29 | [v3110](https://vndb.org/v3110) | v2.47 | ⚪ Not tried |  |
| Iyashinbo The Motion | 癒しん母 The Motion ～世界で一番好きなひと～ | 2010-02-19 | [r6772](https://vndb.org/r6772) | v2.47 | ⚪ Not tried |  |
| Pure Love! | ぴゅあらっ！ | 2010-02-19 | [v2930](https://vndb.org/v2930) | v2.47 | ⚪ Not tried |  |
| Saimin Seikatsu | 催眠生活 ～校則だから仕方ない！？～ | 2010-02-26 | [v3370](https://vndb.org/v3370) | v2.47 | ⚪ Not tried |  |
| Najimi no Oba-chan | 馴染みのオバちゃん | 2010-03-19 | [v3815](https://vndb.org/v3815) | v2.47 | ⚪ Not tried |  |
| Danchi Wife ~Hitozuma Nude Model no Kenshin~ | 団地ワイフ～人妻ヌードモデルの献身～ | 2010-04-23 | [v3917](https://vndb.org/v3917) | v2.47 | ⚪ Not tried |  |
| Ero-On! | えろおん！ | 2010-04-29 | [v11473](https://vndb.org/v11473) | v2.49 | ✅ Plays through | 2026-10-07 |
| Yume Miru Egoist | 夢みるエゴイスト | 2010-05-14 | [v4045](https://vndb.org/v4045) | v2.47 | ⚪ Not tried |  |
| Oshikake! Entremets | おしかけ！アントルメ | 2010-05-28 | [v3824](https://vndb.org/v3824) | v2.47 | ⚪ Not tried |  |
| You~Gaku | よう∽ガク | 2010-05-28 | [v3810](https://vndb.org/v3810) | v2.47 | ⚪ Not tried |  |
| Can Fes! ~Itazura Majo to Naisho no Gakuensai~ | きゃん☆フェス！～いたずら魔女とナイショの学園祭～ | 2010-06-25 | [v4143](https://vndb.org/v4143) | v2.47 | ⚪ Not tried |  |
| Tenki Ane | 天気姉 | 2010-07-09 | [v4543](https://vndb.org/v4543) | v2.47 | ⚪ Not tried |  |
| Ran→Sem | RAN→SEM～白濁デルモ妻のミイラ捕り～ | 2010-07-23 | [v4645](https://vndb.org/v4645) | v2.47 | ⚪ Not tried |  |
| Vestige -Yaiba ni Nokoru wa Kimi no Omokage- | Vestige ―刃に残るは君の面影― | 2010-07-30 | [v3822](https://vndb.org/v3822) | v2.47 | ⚪ Not tried |  |
| Azu Plus | アズプラス | 2010-08-15 | [v7579](https://vndb.org/v7579) | v2.47 | ✅ Plays through | 2026-10-07 |
| Ryoumaden ~Houkago no Rakuen~ | 凌魔伝 ～放課後の楽園～ | 2010-10-15 | [v5325](https://vndb.org/v5325) | v2.47 | ⚪ Not tried |  |
| Ren'ai Saimin ~Tsun na Kanojo ga dereru Saimin~ | 恋愛催眠～ツンな彼女がデレる催眠～ | 2010-11-26 | [v5108](https://vndb.org/v5108) | v2.47 | ⚪ Not tried |  |
| Saint ~Aa Shu yo | セイント ～あぁ主よ、教え子達に堕とされた私をお許し下さい～ | 2010-12-10 | [v5773](https://vndb.org/v5773) | v2.47 | ⚪ Not tried |  |
| Oreimo Plus | 俺妹プラス | 2010-12-31 | [v6035](https://vndb.org/v6035) | v2.47 | ✅ Plays through | 2026-10-07 |
| Bloody Rondo | BLOODY†RONDO | 2011-01-28 | [v5349](https://vndb.org/v5349) | v2.48 | ⚪ Not tried |  |
| Damatte Watashi no Muko ni Nare! | 黙って私のムコになれ！ | 2011-01-28 | [v5289](https://vndb.org/v5289) | v2.47 | ⚪ Not tried |  |
| Gakushoku no Oba-san The Motion | 学食のおばさん The Motion ～母さんの汁の味～ | 2011-02-25 | [r12508](https://vndb.org/r12508) | v2.48 | ⚪ Not tried |  |
| Tanetsuke Mura | 種憑け村 ～白濁神、念仏講ノ儀～ | 2011-02-25 | [v6353](https://vndb.org/v6353) | v2.48 | ⚪ Not tried |  |
| Sensei! Shite Ageru | 先生っ！ シてあげる | 2011-03-04 | [v6444](https://vndb.org/v6444) | v2.48 | ⚪ Not tried |  |
| Onegan! | おねガン！ | 2011-04-22 | [v6585](https://vndb.org/v6585) | v2.48 | ⚪ Not tried |  |
| Aneiro | アネイロ | 2011-06-24 | [v6590](https://vndb.org/v6590) | v2.49 | ✅ Plays through | 2026-10-10 |
| Homu☆Plus | ほむ☆プラス | 2011-08-14 | [v8019](https://vndb.org/v8019) | v2.49 | ✅ Plays through | 2026-10-07 |
| Doushite Daite Kurenai no!? | どうして抱いてくれないのっ!?～女の子だってヤりたいの！～ | 2011-08-26 | [v6991](https://vndb.org/v6991) | v2.49 | ⚪ Not tried |  |
| Shojo Mama | 処女ママ | 2011-11-04 | [v8077](https://vndb.org/v8077) | v2.49 | ⚪ Not tried |  |
| Yuru Plus | ゆるプラス | 2011-11-25 | [v10125](https://vndb.org/v10125) | v2.49 | ✅ Plays through | 2026-10-07 |
| Sena Plus | 星奈プラス | 2011-12-31 | [v10126](https://vndb.org/v10126) | v2.49 | ✅ Plays through | 2026-10-07 |
| Puchipuchi Idol Kouhosei | ぷちぷちアイドル候補生 ～早くシてよ！マネージャーでしょ～ | 2012-02-24 | [v9973](https://vndb.org/v9973) | v2.49 | ⚪ Not tried |  |
| Shinigami no Testament | 死神のテスタメント ～menuet of epistula～ | 2012-02-24 | [v7650](https://vndb.org/v7650) | v2.49 | ⚪ Not tried |  |
| Kuroneko Plus | 黒猫プラス | 2012-05-18 | [v10586](https://vndb.org/v10586) | v2.49 | ✅ Plays through | 2026-10-07 |
| Intruder | イントルーダー ～侵入者～ | 2012-06-22 | [v10533](https://vndb.org/v10533) | v2.49 | ⚪ Not tried |  |
| Nyaru Plus | ニャルプラス | 2012-08-12 | [v10779](https://vndb.org/v10779) | v2.49 | ✅ Plays through | 2026-10-07 |
| Hin wa Bokura no Fuku no Kami | 貧は僕らの福の神 | 2012-08-24 | [v10042](https://vndb.org/v10042) | v2.49 | ⚪ Not tried |  |
| Boku Igai no Otoko o Shiranai Kanojo | 僕以外の男を知らない彼女が他の男に抱かれていた | 2012-08-31 | [v10816](https://vndb.org/v10816) | v2.49 | ⚪ Not tried |  |
| Son of a ☆ Bitch | サノバ☆ビッチ ～早乙女野薔薇ちゃん、マジ天使!～ | 2012-09-07 | [v11089](https://vndb.org/v11089) | v2.49 | ⚪ Not tried |  |
| Zettai Zetsumei Shoujo | 絶体絶命少女 | 2012-09-28 | [v10217](https://vndb.org/v10217) | v2.49 | ⚪ Not tried |  |
| Toriko no Chigiri | 虜ノ契 ～家族のために身体を差し出す姉と妹～ | 2012-12-14 | [v11603](https://vndb.org/v11603) | v2.49 | ⚪ Not tried |  |
| Rikka Plus | 六花プラス | 2012-12-31 | [v11902](https://vndb.org/v11902) | v2.49 | ✅ Plays through | 2026-10-07 |
| Gensou no Idea ~Oratorio Phantasm Historia~ | 幻創のイデア～Oratorio Phantasm Historia～ | 2013-06-28 | [v11881](https://vndb.org/v11881) | v2.49 | ⚪ Not tried |  |
| Tojita Sekai no Tori Colony | 閉じたセカイのトリコロニー | 2013-07-26 | [v12473](https://vndb.org/v12473) | v2.49 | ⚪ Not tried |  |
| Chou Saiminjutsu Gakuen | 超催眠術学園 | 2014-05-30 | [v14614](https://vndb.org/v14614) | v2.49 | ⚪ Not tried |  |
| Majime to Sasayakareru Ore o Osananajimi no Risa | 真面目と囁かれるオレを幼なじみの理彩が性的な意味も込めて陥落していく話 | 2014-07-18 | [v15537](https://vndb.org/v15537) | v2.49 | ⚪ Not tried |  |
| Kanojo ga Ore ni Kureta Mono | 彼女が俺にくれたもの。俺が彼女にあげるもの。 | 2014-09-26 | [v14321](https://vndb.org/v14321) | v2.50 | ⚪ Not tried |  |
| Kinpatsu Tarou Monogatari | 金髪太郎物語 | 2014-10-03 | [v16102](https://vndb.org/v16102) | v2.50 | ⚪ Not tried |  |
| Gyaku Katei Kyoushi | 逆家庭教師 ～彼女は僕の先生にして奴隷～ | 2014-12-12 | [v16306](https://vndb.org/v16306) | v2.50 | ⚪ Not tried |  |
| Maki Fes! | マキフェス！ | 2014-12-30 | [v16484](https://vndb.org/v16484) | v2.50 | ✅ Plays through | 2026-10-07 |
| Shuukatsu Katei Kyoushi | シュウカツ家庭教師 | 2015-03-13 | [v16904](https://vndb.org/v16904) | v2.50 | ⚪ Not tried |  |
| Tsugou no Ii Idol | ツゴウノイイアイドル | 2015-04-24 | [v16469](https://vndb.org/v16469) | v2.50 | ⚪ Not tried |  |
| Sorcery Jokers | ソーサリージョーカーズ | 2015-07-24 | [v16337](https://vndb.org/v16337) | v2.50 | ⚪ Not tried |  |
| Gohoushi Nurse ~Mayonaka no Kyousei Call~ | ご奉仕ナース ～真夜中の強精コール～ | 2015-09-18 | [v17971](https://vndb.org/v17971) | v2.50 | ⚪ Not tried |  |
| Kyuuketsuki no Libra | 吸血姫のリブラ | 2015-10-30 | [v17825](https://vndb.org/v17825) | v2.50 | ⚪ Not tried |  |
| Bitch Nee-chan ga Seijun na Hazu ga Nai! | ビッチ姉ちゃんが清純なはずがないっ！ | 2016-04-28 | [v18781](https://vndb.org/v18781) | v2.50 | 🟡 Starts | 2026-10-11 |
| Lovecha ~Tsumasakidachi de, Hitomi o Tojite~ | らぶちゃ ～つま先立ちで、瞳をとじて～ | 2016-04-28 | [v18590](https://vndb.org/v18590) | v2.50 | ⚪ Not tried |  |
| Signalist Stars!! | しぐなリストスタ～ズ!! | 2016-10-28 | [v19590](https://vndb.org/v19590) | v2.50 | ⚪ Not tried |  |
| Bitch Shimai ga Seijun na Hazu ga Nai!! | ビッチ姉妹が清純なはずがないっ！！ | 2017-01-27 | [v20429](https://vndb.org/v20429) | v2.50 | 🟡 Starts | 2026-10-11 |
| Libra of the Vampire Princess | 吸血姫のリブラ (English) | 2017-05-03 | [r44159](https://vndb.org/r44159) | v2.50 | ⚪ Not tried |  |
| Chiccha na Hanayome ~Mada Mada Tsubomi da mon~ | ちっちゃな花嫁 ～まだまだつぼみだもんっ～ | 2017-05-26 | [v20284](https://vndb.org/v20284) | v2.50 | ⚪ Not tried |  |
| Oni ga Kuru | 鬼がくる。～姉がひん死でピンチです～ | 2017-09-29 | [v21123](https://vndb.org/v21123) | v2.50 | ⚪ Not tried |  |
| Bitch Gakuen ga Seijun na Hazu ga Nai!!? | ビッチ学園が清純なはずがないっ！！？ | 2017-11-24 | [v21429](https://vndb.org/v21429) | v2.50 | 🟡 Starts | 2026-10-11 |
| Sorcery Jokers [English] | ソーサリージョーカーズ (English) | 2018-01-12 | [r47518](https://vndb.org/r47518) | v2.50 | ⚪ Not tried |  |
| Chuuni Hime no Teikoku | 厨二姫の帝国 | 2018-02-23 | [v17900](https://vndb.org/v17900) | v2.50 | ⚪ Not tried |  |
| Re:Rem Plus | Re:レムプラス | 2018-03-31 | [v22991](https://vndb.org/v22991) | v2.50 | ✅ Plays through | 2026-10-07 |
| Nukige Mitai na Shima ni Sunderu [Trial] | 抜きゲーみたいな島に住んでる貧乳はどうすりゃいいですか? 体験版 | 2018-06-22 | [r57518](https://vndb.org/r57518) | v2.50 | ⚪ Not tried |  |
| Kiki Mimi | ききミミ | 2018-07-26 | [v23506](https://vndb.org/v23506) | v2.50 | ⚪ Not tried |  |
| Nukige Mitai na Shima ni Sunderu | 抜きゲーみたいな島に住んでる貧乳はどうすりゃいいですか? | 2018-07-27 | [v22899](https://vndb.org/v22899) | v2.50 | ⚪ Not tried |  |
| Saimin Paradise! | 催眠ぱらだいす! ～催眠術でツゴウノイイ美少女学園性活～ | 2018-09-28 | [v23063](https://vndb.org/v23063) | v2.50 | ⚪ Not tried |  |
| Nukige Mitai na Shima ni Sunderu... 2 | 抜きゲーみたいな島に住んでる貧乳はどうすりゃいいですか? 2 | 2019-07-26 | [v23741](https://vndb.org/v23741) | v2.50 | ⚪ Not tried |  |
| Raillore no Ryakudatsusha | レイルロアの略奪者 | 2019-10-25 | [v24752](https://vndb.org/v24752) | v2.50 | ⚪ Not tried |  |

## Platforms

| Platform | Player | Status | Checked |
|---|---|---|---|
| Windows 10 / 11 | Avalonia (`OpenShiina.Desktop`; the WPF player of 0.2.0 and before is on the branch `legacy-wpf`) | ✅ The games above | 2026-10-08 |
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

**Maki Fes! and Re:Rem Plus (engine v2.50)** 1280 x 720, 53 scenario commands, shown through Direct3D. v2.50 has an
opcode table of its own (read from REMPLUS.EXE: Data/ScnOps/ops_v250.tsv) and 1025 picture
slots; they needed the v2.49 opcodes their START uses (ScnVm.Menus.cs) and four of v2.50
(ScnVm.Engine250.cs: zlib-packed blocks for the saves, characters drawn from the scripts' own
pictures); see engine-notes.md, section 10. The window menu the game puts on its window (exit,
window size) is not shown: the players' own window does both.

**Ao no Juuai** (engine v2.34, aoj.EXE) The oldest engine tried: an opcode table of its own
(Data/ScnOps/ops_v234.tsv) and the handlers where v2.34's differ (ScnVm.Engine234.cs); its
settings are in aoj.INI. In ScnBoot it plays its story to frame 30,000 with choices, and it was
played in the Avalonia player; every embedded x86 routine has a C# version. What is left for it
and other 2.34 games is in docs/todo.md ("Other ShiinaRio games").

**Aneiro** (engine v2.49, ver 1.03a) Skipped to the end by the user (2026-10-10). Every embedded
x86 routine has a C# version.

**Bitch Nee-chan ga Seijun na Hazu ga Nai! (ver 1.02), Bitch Shimai ga Seijun na Hazu ga Nai!!
(ver 1.01), Bitch Gakuen ga Seijun na Hazu ga Nai!!? (ver 1.00)** (engine v2.50) In ScnBoot: the
title and the story (Nee-chan and Gakuen to frame 30,000, Shimai with its opening movie to frame
16,000), nothing missing. In the players their menus ran at 1 frame a second until their SSE2
scaling and blur routines got C# versions (2026-10-11); every embedded x86 routine has one now.
Bitch Nee-chan is the game the translations were tried on (README, "Translations"). Not played
through yet. Two opcodes only these games' START uses were added after the user's crash logs
(2026-10-11): `1072`, the engine's box blur of a picture into another (Shimai's `$EX,PPBLUR`
depth of field: checked in ScnBoot, the background blurred, the character sharp), and `0113`,
whether a file is in a given archive (Gakuen asks its patch archives `BITCHES3_P?.WAR` for a
picture's variant).

**Chuuchuu Nurse** (engine v2.45, CHUCHU.EXE) Its executable's code is encrypted (a `.riox`
stub decrypts it at start), and the game checks its checksum: START stopped with "Program
Revision Error" until `03C2` gave this executable's value. Its 679 opcodes are v2.47's with the
same operands. In ScnBoot: the title, a new game, the story with its pictures, text and music to
frame 9,000.

## Updating

When a game's state changes:

1. In the **Games** table, set its **Status** to the row of the legend that fits and **Checked**
   to the date (YYYY-MM-DD); once it was run, its **ShiinaRio** column is the engine version
   START.SCN checks.
2. Under **Notes**, say what was checked and how (played, skipped with Ctrl, ScnBoot), what was
   added for it, and what is known not to work.
3. When a platform was tried (a phone, Linux, macOS), add or update its row under **Platforms**
   with the device and what ran.
4. If it is the headline of a change, the commit message can name the game; README links here
   and does not list the games itself.

To see which opcodes a game's scripts use that OpenShiina does not run yet, boot it in ScnBoot
(`tests/OpenShiina.ScnBoot`): it stops at the first one and prints it.
