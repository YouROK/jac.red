﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using JacRed.Engine.CORE;
using JacRed.Engine.Parse;
using JacRed.Models.tParse;
using IO = System.IO;
using JacRed.Engine;

namespace JacRed.Controllers.CRON
{
    [Route("cron/torrentby/[action]")]
    public class TorrentByController : BaseController
    {
        static Dictionary<string, List<TaskParse>> taskParse = JsonConvert.DeserializeObject<Dictionary<string, List<TaskParse>>>(IO.File.ReadAllText("Data/temp/torrentby_taskParse.json"));


        #region Parse
        async public Task<string> Parse(int page)
        {
            string log = "";

            // films     - Зарубежные фильмы    | Фильмы
            // movies    - Наши фильмы          | Фильмы
            // serials   - Сериалы              | Сериалы
            // tv        - Телевизор            | ТВ Шоу
            // humor     - Юмор                 | ТВ Шоу
            // cartoons  - Мультфильмы          | Мультфильмы, Мультсериалы
            // anime     - Аниме                | Аниме
            foreach (string cat in new List<string>() { "films", "movies", "serials", "tv", "humor", "cartoons", "anime" })
            {
                int countreset = 0;
                reset: bool res = await parsePage(cat, page);
                if (!res)
                {
                    if (countreset > 5)
                        continue;

                    await Task.Delay(2000);
                    countreset++;
                    goto reset;
                }

                log += $"{cat} - {page}\n";
            }

            return string.IsNullOrWhiteSpace(log) ? "ok" : log;
        }
        #endregion

        #region UpdateTasksParse
        async public Task<string> UpdateTasksParse()
        {
            // films     - Зарубежные фильмы    | Фильмы
            // movies    - Наши фильмы          | Фильмы
            // serials   - Сериалы              | Сериалы
            // tv        - Телевизор            | ТВ Шоу
            // humor     - Юмор                 | ТВ Шоу
            // cartoons  - Мультфильмы          | Мультфильмы, Мультсериалы
            // anime     - Аниме                | Аниме
            foreach (string cat in new List<string>() { "films", "movies", "serials", "tv", "humor", "cartoons", "anime" })
            {
                // Получаем html
                string html = await HttpClient.Get($"http://torrent.by/{cat}/", timeoutSeconds: 10, useproxy: true);
                if (html == null)
                    continue;

                // Максимальное количиство страниц
                int.TryParse(Regex.Match(html, $"<a href=\"/{cat}/\\?page=([0-9]+)\"><b>[0-9]+</b></a></p>").Groups[1].Value, out int maxpages);

                if (maxpages > 0)
                {
                    // Загружаем список страниц в список задач
                    for (int page = 0; page < maxpages; page++)
                    {
                        if (!taskParse.ContainsKey(cat))
                            taskParse.Add(cat, new List<TaskParse>());

                        var val = taskParse[cat];
                        if (val.Find(i => i.page == page) == null)
                            val.Add(new TaskParse(page));
                    }
                }
            }

            IO.File.WriteAllText("Data/temp/torrentby_taskParse.json", JsonConvert.SerializeObject(taskParse));
            return "ok";
        }
        #endregion

        #region ParseAllTask
        static bool _parseAllTaskWork = false;

        async public Task<string> ParseAllTask()
        {
            if (_parseAllTaskWork)
                return "work";

            _parseAllTaskWork = true;

            try
            {
                foreach (var task in taskParse)
                {
                    foreach (var val in task.Value)
                    {
                        if (1 >= DateTime.Now.Hour)
                            break;

                        if (DateTime.Today == val.updateTime)
                            continue;

                        bool res = await parsePage(task.Key, val.page);
                        if (res)
                            val.updateTime = DateTime.Today;
                    }
                }
            }
            catch { }

            _parseAllTaskWork = false;
            return "ok";
        }
        #endregion


        #region parsePage
        async Task<bool> parsePage(string cat, int page)
        {
            string html = await HttpClient.Get($"http://torrent.by/{cat}/?page={page}", useproxy: true);
            if (html == null)
                return false;

            foreach (string row in tParse.ReplaceBadNames(html).Split("<tr class=\"ttable_col").Skip(1))
            {
                #region Локальный метод - Match
                string Match(string pattern, int index = 1)
                {
                    string res = HttpUtility.HtmlDecode(new Regex(pattern, RegexOptions.IgnoreCase).Match(row).Groups[index].Value.Trim());
                    res = Regex.Replace(res, "[\n\r\t ]+", " ");
                    return res.Trim();
                }
                #endregion

                if (string.IsNullOrWhiteSpace(row) || !row.Contains("magnet:?xt=urn"))
                    continue;

                #region Дата создания
                DateTime createTime = default;

                if (row.Contains(">Сегодня</td>"))
                {
                    createTime = DateTime.Today;
                }
                else if (row.Contains(">Вчера</td>"))
                {
                    createTime = DateTime.Today.AddDays(-1);
                }
                else
                {
                    string _createTime = Match(">([0-9]{4}-[0-9]{2}-[0-9]{2})</td>").Replace("-", " ");
                    if (!DateTime.TryParseExact(_createTime, "yyyy MM dd", new CultureInfo("ru-RU"), DateTimeStyles.None, out createTime))
                        continue;
                }

                if (createTime == default)
                    continue;
                #endregion

                #region Данные раздачи
                string url = Match("<a name=\"search_select\" [^>]+ href=\"/([0-9]+/[^\"]+)\"");
                string title = Match("<a name=\"search_select\" [^>]+>([^<]+)</a>");
                string _sid = Match("<font color=\"green\">&uarr; ([0-9]+)</font>");
                string _pir = Match("<font color=\"red\">&darr; ([0-9]+)</font>");
                string sizeName = Match("</td><td style=\"white-space:nowrap;\">([^<]+)</td>");
                string magnet = Match("href=\"(magnet:\\?xt=[^\"]+)\"");

                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(_sid) || string.IsNullOrWhiteSpace(_pir) || string.IsNullOrWhiteSpace(sizeName) || string.IsNullOrWhiteSpace(magnet))
                    continue;

                url = "http://torrent.by/" + url;
                #endregion

                #region Парсим раздачи
                int relased = 0;
                string name = null, originalname = null;

                if (cat == "films")
                {
                    #region Зарубежные фильмы
                    // Код бессмертия / Код молодости / Eternal Code (2019)
                    var g = Regex.Match(title, "^([^/\\(]+) / [^/]+ / ([^/\\(]+) \\(([0-9]{4})\\)").Groups;
                    if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[3].Value))
                    {
                        name = g[1].Value;
                        originalname = g[2].Value;

                        if (int.TryParse(g[3].Value, out int _yer))
                            relased = _yer;
                    }
                    else
                    {
                        // Брешь / Breach (2020)
                        g = Regex.Match(title, "^([^/\\(]+) / ([^/\\(]+) \\(([0-9]{4})\\)").Groups;

                        name = g[1].Value;
                        originalname = g[2].Value;

                        if (int.TryParse(g[3].Value, out int _yer))
                            relased = _yer;
                    }
                    #endregion
                }
                else if (cat == "movies")
                {
                    #region Наши фильмы
                    // Временная связь (2020)
                    // Приключения принца Флоризеля / Клуб самоубийц или Приключения титулованной особы (1979)
                    var g = Regex.Match(title, "^([^/\\(]+) (/ [^/\\(]+)?\\(([0-9]{4})\\)").Groups;
                    name = g[1].Value;

                    if (int.TryParse(g[3].Value, out int _yer))
                        relased = _yer;
                    #endregion
                }
                else if (cat == "serials")
                {
                    #region Сериалы
                    // Перевал / Der Pass / Pagan Peak [S01] (2018)
                    var g = Regex.Match(title, "^([^/\\(\\[]+) / [^/]+ / ([^/\\(\\[]+) \\[[^\\]]+\\] +\\(([0-9]{4})(\\)|-)").Groups;
                    if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[3].Value))
                    {
                        name = g[1].Value;
                        originalname = g[2].Value;

                        if (int.TryParse(g[3].Value, out int _yer))
                            relased = _yer;
                    }
                    else
                    {
                        // Стража / The Watch [01x01-05 из 08] (2020)
                        g = Regex.Match(title, "^([^/\\(\\[]+) / ([^/\\[]+) \\[[^\\]]+\\] +\\(([0-9]{4})(\\)|-)").Groups;
                        if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[3].Value))
                        {
                            name = g[1].Value;
                            originalname = g[2].Value;

                            if (int.TryParse(g[3].Value, out int _yer))
                                relased = _yer;
                        }
                        else
                        {
                            // Стажёры [01-10 из 24] (2019)
                            g = Regex.Match(title, "^([^/\\(\\[]+) \\[[^\\]]+\\] +\\(([0-9]{4})(\\)|-)").Groups;

                            name = g[1].Value;
                            if (int.TryParse(g[2].Value, out int _yer))
                                relased = _yer;
                        }
                    }
                    #endregion
                }
                else if (cat == "cartoons" || cat == "anime" || cat == "tv" || cat == "humor")
                {
                    #region Мультфильмы / Аниме / Телевизор / Юмор
                    if (title.Contains(" / "))
                    {
                        if (title.Contains("[") && title.Contains("]"))
                        {
                            // 	Разочарование / еще название / Disenchantment [S03] (2021)
                            var g = Regex.Match(title, "^([^/]+) / [^/]+ / ([^/\\[]+) \\[[^\\]]+\\] +\\(([0-9]{4})(\\)|-)").Groups;
                            if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[3].Value))
                            {
                                name = g[1].Value;
                                originalname = g[2].Value;

                                if (int.TryParse(g[3].Value, out int _yer))
                                    relased = _yer;
                            }
                            else
                            {
                                // 	Разочарование / Disenchantment [S03] (2021)
                                g = Regex.Match(title, "^([^/]+) / ([^/\\[]+) \\[[^\\]]+\\] +\\(([0-9]{4})(\\)|-)").Groups;

                                name = g[1].Value;
                                originalname = g[2].Value;

                                if (int.TryParse(g[3].Value, out int _yer))
                                    relased = _yer;
                            }
                        }
                        else
                        {
                            // 	Душа / еще название / Soul (2020)
                            var g = Regex.Match(title, "^([^/\\(]+) / [^/]+ / ([^/\\(]+) \\(([0-9]{4})(\\)|-)").Groups;
                            if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[3].Value))
                            {
                                name = g[1].Value;
                                originalname = g[2].Value;

                                if (int.TryParse(g[3].Value, out int _yer))
                                    relased = _yer;
                            }
                            else
                            {
                                // Душа / Soul (2020)
                                // Галактики / Galaxies (2017-2019)
                                g = Regex.Match(title, "^([^/\\(]+) / ([^/\\(]+) \\(([0-9]{4})(\\)|-)").Groups;

                                name = g[1].Value;
                                originalname = g[2].Value;

                                if (int.TryParse(g[3].Value, out int _yer))
                                    relased = _yer;
                            }
                        }
                    }
                    else
                    {
                        if (title.Contains("[") && title.Contains("]"))
                        {
                            // 	Непокоренные [01-04 из 04] (2020)
                            var g = Regex.Match(title, "^([^/\\[]+) \\[[^\\]]+\\] +\\(([0-9]{4})(\\)|-)").Groups;
                            name = g[1].Value;

                            if (int.TryParse(g[2].Value, out int _yer))
                                relased = _yer;
                        }
                        else
                        {
                            // Душа (2020)
                            var g = Regex.Match(title, "^([^/\\(]+) \\(([0-9]{4})(\\)|-)").Groups;
                            name = g[1].Value;

                            if (int.TryParse(g[2].Value, out int _yer))
                                relased = _yer;
                        }
                    }
                    #endregion
                }
                #endregion

                if (!string.IsNullOrWhiteSpace(name))
                {
                    #region types
                    string[] types = null;
                    switch (cat)
                    {
                        case "films":
                        case "movies":
                            types = new string[] { "movie" };
                            break;
                        case "serials":
                            types = new string[] { "serial" };
                            break;
                        case "tv":
                        case "humor":
                            types = new string[] { "tvshow" };
                            break;
                        case "cartoons":
                            types = new string[] { "multfilm", "multserial" };
                            break;
                        case "anime":
                            types = new string[] { "anime" };
                            break;
                    }
                    #endregion

                    int.TryParse(_sid, out int sid);
                    int.TryParse(_pir, out int pir);

                    tParse.AddOrUpdate(new TorrentDetails()
                    {
                        trackerName = "torrentby",
                        types = types,
                        url = url,
                        title = title,
                        sid = sid,
                        pir = pir,
                        sizeName = sizeName,
                        magnet = magnet,
                        createTime = createTime,
                        name = name,
                        originalname = originalname,
                        relased = relased
                    });
                }
            }

            return true;
        }
        #endregion
    }
}
