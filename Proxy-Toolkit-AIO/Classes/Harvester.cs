/*
 *[C#] Proxy Toolkit
 *Copyright (C) 2017  Juan Xuereb
 *
 *This program is free software: you can redistribute it and/or modify
 *it under the terms of the GNU General Public License as published by
 *the Free Software Foundation, either version 3 of the License, or
 *(at your option) any later version.
 *
 *This program is distributed in the hope that it will be useful,
 *but WITHOUT ANY WARRANTY; without even the implied warranty of
 *MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *GNU General Public License for more details.
 *You should have received a copy of the GNU General Public License
 *along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using HtmlAgilityPack;

using System;
using System.Collections.Generic;
using System.Net;

namespace CS_Proxy.Classes.Singlethreaded {
    //Not written for multi-threading - not worth it. Also.. It doesn't really work.
    class Harvester {
        private readonly List<string> Queries = new List<string>();
        private readonly List<string> BlackList = new List<string>();
        private readonly List<string> BlogList = new List<string>();
        private readonly bool VisitBlogs = false; //In blogs/forums proxies are usually not in front page but threads -> if set to true -> visit blog/forum and obtain threads
        private readonly int Pages = 10;
        private readonly int Timeout;

        public HashSet<string> Harvested = new HashSet<string>();

        public Harvester(List<string> queries, int pages, bool visitBlogs, int timeout) {
            Pages = pages;
            VisitBlogs = visitBlogs;
            Timeout = timeout;
            PopulateBlackList();
            if ( visitBlogs )
                PopulateBlogList();

            DateTime today = DateTime.Now;
            for ( var q = 0; q < queries.Count; ++q ) {
                var str = queries[q].ToLower();
                str = str.Replace( "{d}", today.ToString( "d" ) );
                str = str.Replace( "{dd}", today.ToString( "dd" ) );
                str = str.Replace( "{mm}", today.ToString( "MM" ) );
                str = str.Replace( "{mmm}", today.ToString( "MMM" ) );
                str = str.Replace( "{mmmm}", today.ToString( "MMMM" ) );
                str = str.Replace( "{yy}", today.ToString( "yy" ) );
                str = str.Replace( "{yyyy}", today.ToString( "yyyy" ) );

                Queries.Add( str.ToLower() );
            }
        }

        private void PopulateBlogList() {
            BlogList.Clear();
            BlogList.Add( "blog" );
            BlogList.Add( "forum" );
        }

        private bool IsInBloglist(string url) {
            foreach ( var str in BlogList ) {
                if ( url.Contains( str ) )
                    return true;
            }
            return false;
        }

        private void PopulateBlackList() {
            BlackList.Clear();
            BlackList.Add( "javascript" );
            BlackList.Add( "wiki" );
            BlackList.Add( "microsoft" );
            BlackList.Add( "facebook" );
            BlackList.Add( "report" );
            BlackList.Add( "twitter" );
        }

        private bool IsBlacklisted(string url) {
            for ( var i = 0; i < BlackList.Count; ++i ) {
                if ( url.Contains( BlackList[i] ) )
                    return true;
            }
            return false;
        }

        public void GetURLs() {
            var wc = new MyWebClient();
            wc.Timeout = Timeout;

            var qNo = 0;
            foreach ( var query in Queries ) {
                qNo++;
                var searchURL = string.Concat( "http://www.bing.com/search?q=", query );

                for ( var page = 1; page <= Pages; ++page ) {
                    Console.WriteLine( "Harvesting URLs from Page {0}", page.ToString() );
                    var html = string.Empty;
                    try {
                        html = wc.DownloadString( searchURL ).Replace( "&amp;", "&" );
                    } catch ( WebException ) { html = string.Empty; } catch ( NotSupportedException ) { html = string.Empty; } catch ( ArgumentNullException ) { html = string.Empty; }
                    if ( html == string.Empty )
                        continue;

                    var nextPageID = "title=\"Next page\" href=\"";
                    var nextPageIndex = html.IndexOf( nextPageID );
                    if ( nextPageIndex > 0 ) {
                        var start = nextPageIndex + nextPageID.Length;
                        var end = html.IndexOf( "\"", start );
                        searchURL = string.Concat( "http://www.bing.com", html.Substring( start, end - start ) ).Replace( "&amp;", "&" );
                    } else
                        searchURL = string.Empty;


                    var docu = new HtmlDocument();
                    docu.LoadHtml( html );
                    foreach ( HtmlNode link in docu.DocumentNode.SelectNodes( "//a[@href]" ) ) {
                        // Get the value of the HREF attribute
                        var url = link.GetAttributeValue( "href", string.Empty );

                        if ( Uri.IsWellFormedUriString( url, UriKind.Absolute ) ) {
                            if ( !IsBlacklisted( url ) && Harvested.Add( url ) ) {
                                Console.WriteLine( " - {0}", url );
                                Program.UI.AddURL( url, qNo, Queries.Count, page, query );

                                if ( IsInBloglist( url ) ) {
                                    var _url = url.Replace( "http://", "" ).Replace( "https://", "" );
                                    var baseUrl = string.Concat( "http://", _url.Contains( "/" ) ? _url.Substring( 0, _url.IndexOf( "/" ) ) : _url );
                                    var docu2 = new HtmlDocument();
                                    var newhtml = string.Empty;
                                    try {
                                        newhtml = wc.DownloadString( url ).Replace( "&amp;", "&" );
                                    } catch ( WebException ) { html = string.Empty; } catch ( NotSupportedException ) { html = string.Empty; } catch ( ArgumentNullException ) { html = string.Empty; }
                                    if ( newhtml == string.Empty )
                                        continue;

                                    docu2.LoadHtml( newhtml );
                                    foreach ( HtmlNode link2 in docu2.DocumentNode.SelectNodes( "//a[@href]" ) ) {
                                        var url2 = link2.GetAttributeValue( "href", string.Empty );
                                        if ( url2.Length <= 1 )
                                            continue;

                                        if ( !Uri.IsWellFormedUriString( url2, UriKind.Absolute ) ) //needs concatenating
                                        {
                                            if ( url2[0] != '/' )
                                                url2 = string.Concat( url, url2 );
                                            else
                                                url2 = string.Concat( baseUrl, url2 );
                                        }

                                        if ( Uri.IsWellFormedUriString( url2, UriKind.Absolute ) ) {
                                            if ( !IsBlacklisted( url ) && Harvested.Add( url ) ) {
                                                Console.WriteLine( " - {0}", url );
                                                Program.UI.AddURL( url, qNo, Queries.Count, page, query );
                                            }
                                        } else
                                            Console.WriteLine( "NO - " + url2 );
                                    }
                                }
                            }
                        }
                    }

                    if ( searchURL == string.Empty )
                        break;
                }

            } //next query

            Program.UI.ReportHarvest();
        }
    }
}
