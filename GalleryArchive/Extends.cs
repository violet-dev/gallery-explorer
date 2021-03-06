﻿// This source code is a part of Gallery Explorer Project.
// Copyright (C) 2020. rollrat. Licensed under the MIT Licence.

using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace GalleryExplorer.Core
{
    public static class Extends
    {
        public static int ToInt(this string str) => Convert.ToInt32(str);

        public static string MyText(this HtmlNode node) =>
            string.Join("", node.ChildNodes.Where(x => x.Name == "#text").Select(x => x.InnerText.Trim()));

        public static HtmlNode ToHtmlNode(this string html)
        {
            var document = new HtmlDocument();
            document.LoadHtml(html);
            return document.DocumentNode;
        }
    }
}
