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

using CS_Proxy.Classes.Multithreaded;
using CS_Proxy.Classes.Singlethreaded;
using CS_Proxy.Lists;
using CS_Proxy.Proxy;

using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;


namespace CS_Proxy {
    public partial class Form1 : Form {
        readonly URLManager URLMgr = new URLManager(); //Holds URLs which will need scraping
        readonly HashSet<string> ProxiesScraped = new HashSet<string>(); //Proxies scraped from URLManager [DONT WANT DUPES]

        readonly ProxyManager ProxyMgr = new ProxyManager(); //Holds Proxies which will need scanning
        //HashSet<MyProxy> ProxiesToScan = new HashSet<MyProxy>(); //Proxies to be scanned (same as those in ProxyMgr)
        readonly ProxyFilter filter = new ProxyFilter( "dangerous_ip_ranges.txt" );
        readonly System.Timers.Timer _uiTimer = new System.Timers.Timer( 500 );

        public Form1() {
            InitializeComponent();
        }

        private void Form1_Load(object sender, EventArgs e) {
            _uiTimer.Elapsed += async (_, __) => await UpdateScannerUI();
            proxyView.ContextMenuStrip = contextMenuStrip1;
            ScapePanelUI();
            EnableScanUI();
            proxyJudgesComboBox.SelectedIndex = 0;
            statusLbl.Text = "Status: Idle";
            scanPercentLbl.Text = "";
            queriesLeftLbl.Text = "";

            if ( queryRTBox.Text.Contains( "{" ) )
                HighlightQueryBox();

            if ( !HasInternet() ) {
                statusLbl.Text = "Status: No Internet";
                MessageBox.Show( "You do NOT have access to the internet.\nPress 'OK' to Terminate.", "No Internet", MessageBoxButtons.OK, MessageBoxIcon.Warning );
                Application.Exit();
            }

            if ( !filter.IsInitialized ) {
                removeDangCheck.Checked = false;
                removeDangCheck.Visible = false;
            }
        }

        [DllImport( "wininet.dll" )]
        private extern static bool InternetGetConnectedState(out int Description, int ReservedValue);

        //Uses wininet.dll (best method)
        private static bool HasInternet() {
            return InternetGetConnectedState( out var _, 0 );
        }

        private void ScapePanelUI() {
            scrapedPanel.Visible = ProxiesScraped.Count > 0;
            scrapeBtn.Enabled = URLMgr.Count > 0;

            urlsToScrapeLbl.Text = $"URLs To Scrape: {URLMgr.Count}";
            urlsToScrapeLbl.Location = new Point( importURLsBtn.Location.X - urlsToScrapeLbl.Width - 15, urlsToScrapeLbl.Location.Y );
        }

        private void ImportURLsBtn_Click(object sender, EventArgs e) {
            var ofd = new OpenFileDialog();
            ofd.Title = "URLs To Scrape";
            ofd.Filter = "Text Files|*.txt";
            ofd.Multiselect = true;

            if ( ofd.ShowDialog() == DialogResult.OK ) {
                URLMgr.Clear();
                uint badUrls = 0;
                foreach ( var file in ofd.FileNames ) {
                    using ( var fs = new FileStream( file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ) ) //IMP: Add Exception Checks
                    using ( var sr = new StreamReader( fs, Encoding.Default ) ) {
                        string url;
                        while ( (url = sr.ReadLine()) != null ) {
                            if ( Uri.IsWellFormedUriString( url, UriKind.Absolute ) && !URLMgr.Contains( url ) )
                                URLMgr.Add( url );
                            else
                                badUrls++;
                        }
                    }
                }

                //Update UI
                ScapePanelUI();

                MessageBox.Show( string.Concat( $"A total of {URLMgr.Count} URLs have been imported for scraping.",
                    badUrls > 0 ? $"\n{badUrls} URLs seem to be invalid and were thus omitted." : string.Empty ),
                    "Import URLs", MessageBoxButtons.OK, MessageBoxIcon.Information );
            }
        }

        private static readonly object scrapeUILock = new object();
        private void UpdateScraperUI() {
            lock ( scrapeUILock ) {
                if ( Scraper.TerminateThreads )
                    scrapeBtn.Invoke( new Action( () => scrapeBtn.Text = $"Threads = {Scraper.Threads}" ) );

                // scrapedListBox.Invoke(new Action(() => scrapedListBox.SelectedIndex = scrapedListBox.Items.Count - 1)); //force scroll down
                scrapedLbl.Invoke( new Action( () => scrapedLbl.Text = $"Scraped: {ProxiesScraped.Count}" ) ); //update label
                scrapeProgBar.Invoke( new Action( () => scrapeProgBar.Value = Scraper.URLScraped ) ); //update progress bar

                if ( statusStrip1.Parent.InvokeRequired ) {
                    var statuslbl = $"{Scraper.URLScraped} out of {URLMgr.Count} URLs Scraped!";
                    statusStrip1.Parent.Invoke( new MethodInvoker( delegate { statusLbl.Text = statuslbl; } ) );
                }
            }
        }


        private void ClearScrapeList() {
            ProxiesScraped.Clear();
            scrapedListBox.Items.Clear();
            scrapedLbl.Text = $"Scraped: {ProxiesScraped.Count}";
            ScapePanelUI();
        }
        private void ClearScrapedBtn_Click(object sender, EventArgs e) {
            ClearScrapeList();
        }

        private static List<List<string>> Spliterino(List<string> source, int chunkSize) {
            return source
                .Select( (x, i) => new { Index = i, Value = x } )
                .GroupBy( x => x.Index / chunkSize )
                .Select( x => x.Select( v => v.Value ).ToList() )
                .ToList();
        }

        private void ScrapeBtn_Click(object sender, EventArgs e) {
            if ( scrapeBtn.Text.StartsWith( "Stop" ) ) {
                scrapeBtn.Invoke( new Action( () => scrapeBtn.Enabled = false ) ); //spam click
                scrapeBtn.Text = "Stopping...";
                Scraper.TerminateThreads = true;
                return;
            }

            if ( Convert.ToInt32( threadCountNumUpDown.Value ) > URLMgr.Count )
                threadCountNumUpDown.Invoke( new Action( () => threadCountNumUpDown.Value = URLMgr.Count ) );

            if ( !HasInternet() ) {
                statusLbl.Text = "Status: No Internet";
                MessageBox.Show( "You do NOT have access to the internet.", "No Internet", MessageBoxButtons.OK, MessageBoxIcon.Warning );
                return;
            }

            ClearScrapeList();
            scrapeProgBar.Maximum = URLMgr.Count;
            scrapedPanel.Visible = true;

            var TotalThreads = Convert.ToInt32( threadCountNumUpDown.Value );
            var Timeout = Convert.ToInt32( timeoutNumUpDown.Value ) * 1000;

            Scraper.TerminateThreads = false;
            Scraper.PauseThreads = false;

            //Create the Threads
            if ( Thread.CurrentThread.Name == null )
                Thread.CurrentThread.Name = "FormThread";

            var threads = new Thread[TotalThreads];
            for ( var t = 0; t < TotalThreads; ++t ) {
                var scraper = new Scraper( URLMgr, Timeout );
                var thread = new Thread( new ThreadStart( scraper.GetProxies ) );
                thread.IsBackground = true; //imp so all auto terminate on formClosing()
                thread.Name = $"Scraper_#{t}";
                threads[t] = thread;
                Thread.Sleep( 10 );
            }

            //Execute the Threads ALL @ once (better flow)
            for ( var t = 0; t < TotalThreads; ++t ) {
                threads[t].Start();
                Thread.Sleep( 10 );
            }
        }

        private void SaveScrapedBtn_Click(object sender, EventArgs e) {
            var sfd = new SaveFileDialog();
            sfd.FileName = "ProxiesScraped.txt";
            sfd.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";

            if ( sfd.ShowDialog() == DialogResult.OK ) {
                using ( var sw = new StreamWriter( sfd.FileName ) ) {
                    foreach ( var proxy in ProxiesScraped )
                        sw.WriteLine( proxy );
                }
            }
        }

        private void ScrapedToScanner() {
            if ( ProxiesScraped.Count == 0 ) {
                MessageBox.Show( "Nothing to send to Scanner... Try scraping some proxies or import a list from the Scanner tab.", "No Scraped Proxies", MessageBoxButtons.OK, MessageBoxIcon.Warning );
                return;
            }

            if ( removeDangCheck.Checked ) {
                var nonDangerous = new HashSet<string>();
                foreach ( var proxy in ProxiesScraped ) //looping thru hashset not encouraged but w/e
                {
                    if ( proxy != null && !filter.IsDangerous( proxy ) )
                        nonDangerous.Add( proxy );
                }
                ProxyMgr.Initialize( nonDangerous ); //Don't have to create new instance because it already is a new instance
            } else
                ProxyMgr.Initialize( new HashSet<string>( ProxiesScraped ) ); //New instance, otherwise we have reference to ProxiesScraped which could be problematic if simult. scanning & scraping

            EnableScanUI();
            tabControl1.Invoke( new Action( () => tabControl1.SelectedIndex = 1 ) );
        }
        private void ToScannerBtn_Click(object sender, EventArgs e) {
            ScrapedToScanner();
            if ( scrapedListBox.Items.Count > ProxyMgr.Count )
                MessageBox.Show( $"{scrapedListBox.Items.Count - ProxyMgr.Count} DANGEROUS Proxies were removed.", "Filter", MessageBoxButtons.OK, MessageBoxIcon.Information );
            else
                System.Media.SystemSounds.Beep.Play();
        }

        private void EnableScanUI() {
            proxiesToScanLbl.Invoke( new Action( () => proxiesToScanLbl.Text = $"Proxies to Scan: {ProxyMgr.Count}" ) );
            using ( Graphics g = CreateGraphics() ) {
                SizeF lblsize = g.MeasureString( proxiesToScanLbl.Text, proxiesToScanLbl.Font, 1000 );
                var loc = new Point( ((importProxiesBtn.Width - Convert.ToInt32( lblsize.Width )) / 2) + importProxiesBtn.Location.X, proxiesToScanLbl.Location.Y );
                proxiesToScanLbl.Invoke( new Action( () => proxiesToScanLbl.Location = loc ) );
            }
            scanProxiesBtn.Invoke( new Action( () => scanProxiesBtn.Enabled = ProxyMgr.Count > 0 ) );
        }

        private void ImportProxiesBtn_Click(object sender, EventArgs e) {
            var ofd = new OpenFileDialog();
            ofd.Title = "Proxies";
            ofd.Filter = "Text Files|*.txt";
            ofd.Multiselect = true;

            if ( ofd.ShowDialog() == DialogResult.OK ) {
                ProxyMgr.Clear();
                uint badProxies = 0;
                uint duplicateProxies = 0;
                uint dangerousProxies = 0;
                foreach ( var file in ofd.FileNames ) {
                    using ( var fs = new FileStream( file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ) )
                    using ( var sr = new StreamReader( fs, Encoding.Default ) ) {
                        string proxy;
                        while ( (proxy = sr.ReadLine()) != null ) {
                            var myProxy = new MyProxy( proxy, true );
                            if ( myProxy.IsMalformed )
                                badProxies++;
                            else {
                                if ( removeDangCheck.Checked ) {
                                    if ( filter.IsDangerous( myProxy.ToString() ) ) {
                                        dangerousProxies++;
                                        continue;
                                    }
                                }
                                if ( !ProxyMgr.Add( myProxy.ToString() ) ) //adding to hashset
                                    duplicateProxies++;
                            }
                        }
                    }
                }

                //Update UI
                EnableScanUI();

                MessageBox.Show( string.Concat( $"A total of {ProxyMgr.Count} Proxies have been imported for scanning.",
                    (badProxies + duplicateProxies) > 0 ? $"\n - {badProxies} bad proxies and {duplicateProxies} duplicates were removed!" : string.Empty,
                    dangerousProxies > 0 ? $"\n - {dangerousProxies} DANGEROUS Proxies were also removed!" : string.Empty ),
                    "Import Proxies", MessageBoxButtons.OK, MessageBoxIcon.Information );
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e) {
        }

        private void ClearProxyView() {
            proxyView.Items.Clear();
        }

        DateTime ScanStart = DateTime.Now;
        private void Scan() {
            if ( scanProxiesBtn.Text.StartsWith( "Stop" ) ) {
                scanProxiesBtn.Invoke( new Action( () => scanProxiesBtn.Enabled = false ) ); //spam click
                scanProxiesBtn.Text = "Stopping...";
                Scanner.TerminateThreads = true;
                return;
            }

            if ( Convert.ToInt32( threadsProxiesNumUpDown.Value ) > ProxyMgr.Count )
                threadsProxiesNumUpDown.Invoke( new Action( () => threadsProxiesNumUpDown.Value = ProxyMgr.Count ) );

            ClearProxyView();
            if ( statusStrip2.Parent.InvokeRequired ) {
                statusStrip2.Parent.Invoke( new MethodInvoker( delegate { scanProgBar.Maximum = ProxyMgr.Count; } ) );
                statusStrip2.Parent.Invoke( new MethodInvoker( delegate { scanPercentLbl.Text = "0%"; } ) );
            } else {
                scanProgBar.Maximum = ProxyMgr.Count;
                scanPercentLbl.Text = "0%";
            }
            scanProxiesBtn.Invoke( new Action( () => scanProxiesBtn.Text = "Stop" ) );
            threadsProxiesNumUpDown.Invoke( new Action( () => threadsProxiesNumUpDown.Enabled = false ) );
            timeoutProxiesNumUpDown.Invoke( new Action( () => timeoutProxiesNumUpDown.Enabled = false ) );
            aliveLbl.Invoke( new Action( () => aliveLbl.Text = "Alive = 0" ) );
            deadLbl.Invoke( new Action( () => deadLbl.Text = "Dead = 0" ) );

            var TotalThreads = Convert.ToInt32( threadsProxiesNumUpDown.Value );
            MyProxy.Timeout = Convert.ToInt32( timeoutProxiesNumUpDown.Value ) * 1000;
            CalcTimeoutQuartiles();

            _uiTimer.Start();

            //Create the Threads
            if ( Thread.CurrentThread.Name == null )
                Thread.CurrentThread.Name = "FormThread";

            ProxyMgr.Initialize( null ); //reference not a new copy

            Scanner.TerminateThreads = false;
            Scanner.PauseThreads = false;

            //Create the Threads
            var threads = new Thread[TotalThreads];
            for ( var t = 0; t < TotalThreads; ++t ) {
                var scanner = new Scanner( ProxyMgr );
                var thread = new Thread( new ThreadStart( scanner.Start ) );
                thread.IsBackground = true; //imp so all auto terminate on formClosing()
                thread.Name = $"Scanner_#{t}";
                threads[t] = thread;
                Thread.Sleep( 10 );
            }

            //Execute the Threads ALL @ once (better flow)
            for ( var t = 0; t < TotalThreads; ++t ) {
                threads[t].Start();
                Thread.Sleep( 10 );
            }

            ScanStart = DateTime.Now;
        }

        private void ScanProxiesBtn_Click(object sender, EventArgs e) {
            if ( !HasInternet() ) {
                statusLbl.Text = "Status: No Internet";
                MessageBox.Show( "You do NOT have access to the internet.", "No Internet", MessageBoxButtons.OK, MessageBoxIcon.Warning );
                return;
            }
            Scan();
        }

        private void CalcTimeoutQuartiles() {
            timeout50 = MyProxy.Timeout / 2;
            timeout25 = MyProxy.Timeout / 4;
            timeout75 = timeout25 + timeout50;
        }

        private void TimeoutProxiesNumUpDown_ValueChanged(object sender, EventArgs e) {
            MyProxy.Timeout = Convert.ToInt32( timeoutNumUpDown.Value ) * 1000;
            CalcTimeoutQuartiles();
        }

        private void ProxyJudgesComboBox_SelectedIndexChanged(object sender, EventArgs e) {
            MyProxy.Judge = proxyJudgesComboBox.SelectedItem.ToString();
        }

        private void HttpCheck_CheckedChanged(object sender, EventArgs e) {
            MyProxy.ScanHTTP = httpCheck.Checked;
            scanProxiesBtn.Enabled = !(!httpCheck.Checked && !socksCheck.Checked || ProxyMgr.Count <= 0);
        }

        private void SocksCheck_CheckedChanged(object sender, EventArgs e) {
            MyProxy.ScanSOCKS = socksCheck.Checked;
            scanProxiesBtn.Enabled = !(!httpCheck.Checked && !socksCheck.Checked || ProxyMgr.Count <= 0);
        }

        private void ClearToolStripMenuItem_Click(object sender, EventArgs e) {
            proxyView.Items.Clear();
            aliveLbl.Text = "Alive = 0";
            deadLbl.Text = "Dead = 0";
        }

        private void ToClipboardToolStripMenuItem_Click(object sender, EventArgs e) {
            var outputForm = new Form2( true, false, "To Clipboard", ProxyMgr );
            outputForm.Show();
        }

        private void SaveToolStripMenuItem_Click(object sender, EventArgs e) {
            var outputForm = new Form2( false, true, "Save", ProxyMgr );
            outputForm.Show();
        }

        private void QuestionScraperBtn_Click(object sender, EventArgs e) {
            MessageBox.Show( "This tab is used to scrape proxies off a given list of URLs.\nJust hit the 'Import URLs' button and click the 'Scrape' button!",
                "?", MessageBoxButtons.OK, MessageBoxIcon.Information );
        }

        private void HarvestBtn_Click(object sender, EventArgs e) {
            if ( !HasInternet() ) {
                MessageBox.Show( "You do NOT have access to the internet.", "No Internet", MessageBoxButtons.OK, MessageBoxIcon.Warning );
                return;
            }

            var queries = queryRTBox.Text.Split( new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries ).ToList();
            if ( queries.Count > 0 ) {
                harvestProgBar.Visible = true;
                harvestProgBar.Maximum = queries.Count;
                harvestStatusLbl.Text = "Searching...";
                harvestBtn.Enabled = false;
                var harvester = new Harvester( queries, Convert.ToInt32( pagesNumUpDown.Value ), false, Convert.ToInt32( timeoutNumUpDown.Value * 1000 ) );
                var thread = new Thread( new ThreadStart( harvester.GetURLs ) );
                thread.IsBackground = true;
                thread.Name = "HarvesterThread";
                Thread.Sleep( 10 );

                thread.Start();
            }
        }

        private void ClearHarvestBtn_Click(object sender, EventArgs e) {
            harvestBox.Items.Clear();
            queriesLeftLbl.Text = "";
            urlsHarvestedLbl.Text = "URLs: 0";
            harvestProgBar.Value = 0;
            harvestProgBar.Visible = false;
            harvestStatusLbl.Text = "Status: Idle";
        }

        #region Thread Safe UI Calls [+]

        public void ReportHarvest() {
            harvestBtn.Invoke( new Action( () => harvestBtn.Enabled = true ) );

            MessageBox.Show( $"URL harvesting has completed!\nObtained a total of {harvestBox.Items.Count} URLs.",
                "Harvesting Complete", MessageBoxButtons.OK, MessageBoxIcon.Information );
            queriesLeftLbl.Invoke( new Action( () => queriesLeftLbl.Text = "" ) );

            if ( statusStrip3.Parent.InvokeRequired ) {
                statusStrip3.Parent.Invoke( new MethodInvoker( delegate { harvestProgBar.Value = 0; } ) );
                statusStrip3.Parent.Invoke( new MethodInvoker( delegate { harvestProgBar.Visible = false; } ) );
                statusStrip3.Parent.Invoke( new MethodInvoker( delegate { harvestStatusLbl.Text = "Complete!"; } ) );
            }
        }

        private void UpdateHarvestUI(int queryNo, int totalQueries, int pageNo, string query) {
            queriesLeftLbl.Invoke( new Action( () => queriesLeftLbl.Text = $"Queries: {queryNo}/{totalQueries}\nPage: {pageNo}" ) );
            urlsHarvestedLbl.Invoke( new Action( () => urlsHarvestedLbl.Text = $"URLs: {harvestBox.Items.Count}" ) );

            if ( statusStrip3.Parent.InvokeRequired ) {
                statusStrip3.Parent.Invoke( new MethodInvoker( delegate { harvestProgBar.Value = queryNo; } ) );
                statusStrip3.Parent.Invoke( new MethodInvoker( delegate { harvestStatusLbl.Text = string.Concat( query, "  " ); } ) );
            }
        }

        public void AddURL(string url, int queryNo, int totalQueries, int pageNo, string query) {
            harvestBox.Invoke( new Action( () => harvestBox.Items.Add( url ) ) );
            harvestBox.Invoke( new Action( () => harvestBox.SelectedIndex = harvestBox.Items.Count - 1 ) );
            UpdateHarvestUI( queryNo, totalQueries, pageNo, query );
        }

        public void AddProxy(MyProxy proxy) {
            if ( ProxiesScraped.Add( proxy.ToString() ) ) //if added to hashset and not a duplicate
            {
                scrapedListBox.Invoke( new Action( () => scrapedListBox.Items.Add( proxy.ToString() ) ) ); //add to listbox UI
                UpdateScraperUI();
            }
        }

        public void UpdateScrapeBtns() {
            if ( Scraper.Threads == 0 ) {
                UpdateScraperUI(); //call this first before modifying other controls
                statusLbl.Text = "Finished Scraping!";
                scrapeBtn.Invoke( new Action( () => scrapeBtn.Text = "Scrape" ) );
                scrapeProgBar.Invoke( new Action( () => scrapeProgBar.Value = 0 ) );

                scrapeBtn.Invoke( new Action( () => scrapeBtn.Enabled = true ) );
                if ( !autoScanCheck.Checked ) {
                    MessageBox.Show( $"Scraping Finished!!!\nScraped a total of {ProxiesScraped.Count} proxies!!!\n{Scraper.BadURLs} were invalid/errorsome URLs and {Scraper.EmptyURLs} contained no proxies...",
                        "Scraper", MessageBoxButtons.OK, MessageBoxIcon.Information );
                } else {
                    //AUTO-SCAN
                    ScrapedToScanner();
                    Scan();
                }
            } else
                scrapeBtn.Invoke( new Action( () => scrapeBtn.Text = Scraper.TerminateThreads ? "Stopping..." : "Stop" ) );
        }

        int timeout50 = MyProxy.Timeout / 2;
        int timeout25 = MyProxy.Timeout / 4;
        int timeout75 = Convert.ToInt32( (float) MyProxy.Timeout / (float) 1.333334 );
        public void AddToListView(MyProxy p) {
            //0=Proxy 1=Latency 2=Type 3=Level
            var type = p.Type.ToString().ToUpper();// == xNet.ProxyType.Http ? "HTTP" : (p.Type == xNet.ProxyType.Socks5 ? "SOCKS5" : "SOCKS4");
            string[] row = { p.ToString(), p.Latency.ToString(),
                type, p.AnonLevel.ToString() };
            var item = new ListViewItem( row );
            item.UseItemStyleForSubItems = false;

            Color col = Color.Black;
            if ( p.Latency > MyProxy.Timeout )
                col = Color.DarkGray;
            else if ( p.Latency <= MyProxy.Timeout && p.Latency > timeout75 )
                col = Color.Red;
            else if ( p.Latency < timeout75 && p.Latency > timeout50 )
                col = Color.Orange;
            else if ( p.Latency < timeout50 && p.Latency > timeout25 )
                col = Color.Blue;
            else if ( p.Latency < timeout25 )
                col = Color.Green;

            item.Font = new Font( FontFamily.GenericSansSerif, 8.0F, FontStyle.Regular ); ;
            item.SubItems[1].ForeColor = col;
            item.SubItems[1].Font = new Font( FontFamily.GenericSansSerif, 8.0F, FontStyle.Italic );
            item.SubItems[2].Font = new Font( FontFamily.GenericSansSerif, 8.0F, FontStyle.Bold );
            item.SubItems[2].Font = new Font( FontFamily.GenericSansSerif, 7.0F, FontStyle.Bold );
            item.SubItems[3].Font = new Font( FontFamily.GenericSansSerif, 8.0F, FontStyle.Bold );

            proxyView.Invoke( new Action( () => proxyView.Columns[1].TextAlign = HorizontalAlignment.Center ) ); //centre latency
            proxyView.Invoke( new Action( () => proxyView.Columns[2].TextAlign = HorizontalAlignment.Center ) ); //centre type

            if ( p.AnonLevel == Anonymity.Transparent ) {
                item.SubItems[3].Font = new Font( FontFamily.GenericSansSerif, 7.0F, FontStyle.Bold );
                item.SubItems[3].ForeColor = Color.DarkGray;
            } else if ( p.AnonLevel == Anonymity.High )
                item.SubItems[3].ForeColor = Color.Blue;
            if ( p.AnonLevel == Anonymity.Elite )
                item.SubItems[3].ForeColor = Color.Purple;

            if ( type == "HTTP" )
                item.SubItems[2].ForeColor = Color.Cyan;
            else //SOCKS
                item.SubItems[2].ForeColor = Color.Lime;

            proxyView.Invoke( new Action( () => proxyView.Items.Add( item ) ) );
            proxyView.Invoke( new Action( () => proxyView.Items[proxyView.Items.Count - 1].EnsureVisible() ) );
            // proxyView.Invoke(new Action(() => proxyView.Items[proxyView.Items.Count - 1].Selected = true));
            //proxyView.Invoke(new Action(() => proxyView.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent)));
            //proxyView.Invoke(new Action(() => proxyView.AutoResizeColumns(ColumnHeaderAutoResizeStyle.HeaderSize)));
        }

        private Task UpdateScannerUI() { //This is updated periodically (on separate thread/timer) rather than potentially hundreds of threads calling this
            if ( Scanner.TerminateThreads )
                scanProxiesBtn.Invoke( new Action( () => scanProxiesBtn.Text = $"Threads = {Scanner.Threads}" ) );

            aliveLbl.Invoke( new Action( () => aliveLbl.Text = $"Alive = {Scanner.Alive}    |    Anonymous = {Scanner.High + Scanner.Elite}" ) );
            deadLbl.Invoke( new Action( () => deadLbl.Text = $"Dead = {Scanner.Dead}" ) );

            if ( statusStrip2.Parent.InvokeRequired ) {
                statusStrip2.Parent.Invoke( new MethodInvoker( delegate { scanProgBar.Value = Scanner.Scanned; } ) );
                if ( Scanner.Scanned != 0 ) {
                    var percent = (Scanner.Scanned * 100.0f) / ProxyMgr.Count;

                    var seconds = (DateTime.Now - ScanStart).TotalSeconds; //use double coz of DivByZero Exc
                    var leftToScan = ProxyMgr.Count - Scanner.Scanned;
                    var secondsLeft = (leftToScan * seconds) / Scanner.Scanned;
                    var ts = TimeSpan.FromSeconds( secondsLeft );
                    statusStrip2.Parent.Invoke( new MethodInvoker( delegate { scanPercentLbl.Text = $"ETA: {ts:hh\\:mm\\:ss}  [{percent:0.00}%]"; } ) );
                }
            }

            if ( Scanner.Threads == 0 && Scanner.Scanned != 0 ) {
                _uiTimer.Stop();
                scanProxiesBtn.Invoke( new Action( () => scanProxiesBtn.Text = "Scan" ) );
                scanProxiesBtn.Invoke( new Action( () => scanProxiesBtn.Enabled = true ) );
                threadsProxiesNumUpDown.Invoke( new Action( () => threadsProxiesNumUpDown.Enabled = true ) );
                timeoutProxiesNumUpDown.Invoke( new Action( () => timeoutProxiesNumUpDown.Enabled = true ) );

                MessageBox.Show( string.Concat( $"Found {Scanner.Alive} working proxies!",
                    Scanner.Https > 0 ? $"\nHTTP working = {Scanner.Https}" : string.Empty,
                    Scanner.Socks > 0 ? $"\nSOCKS working = {Scanner.Socks}" : string.Empty ),
                    "Scanner", MessageBoxButtons.OK, MessageBoxIcon.Information );

                if ( statusStrip2.Parent.InvokeRequired ) {
                    statusStrip2.Parent.Invoke( new MethodInvoker( delegate { scanProgBar.Value = 0; } ) );
                    statusStrip2.Parent.Invoke( new MethodInvoker( delegate { scanPercentLbl.Text = "Complete"; } ) );
                }
            }

            return Task.CompletedTask;
        }
        #endregion

        private void DeleteHarvestItemBtn_Click(object sender, EventArgs e) {
            if ( harvestBox.SelectedIndex == -1 )
                return;
            harvestBox.Items.RemoveAt( harvestBox.SelectedIndex );
            urlsHarvestedLbl.Invoke( new Action( () => urlsHarvestedLbl.Text = string.Concat( "URLs: ", harvestBox.Items.Count.ToString() ) ) );
        }

        private void SaveHarvestBtn_Click(object sender, EventArgs e) {
            var sfd = new SaveFileDialog();
            sfd.FileName = "URLs.txt";
            sfd.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";

            if ( sfd.ShowDialog() == DialogResult.OK ) {
                using ( var sw = new StreamWriter( sfd.FileName ) ) {
                    foreach ( var url in harvestBox.Items )
                        sw.WriteLine( url.ToString() );
                }
            }
        }

        private void LinkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            System.Diagnostics.Process.Start( "https://msdn.microsoft.com/en-us/library/ff795620.aspx" );
        }

        private void ToScraperBtn_Click(object sender, EventArgs e) {
            URLMgr.Clear();
            foreach ( var o in harvestBox.Items )
                URLMgr.Add( o.ToString() );

            ScapePanelUI();
            tabControl1.SelectedIndex = 0;
        }

        private void LinkLabel2_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            DateTime today = DateTime.Now;
            MessageBox.Show( $"{{d}} = {today:d}\n{{dd}} = {today:dd}\n{{mm}} = {today:MM}\n{{mmm}} = {today:MMM}\n{{mmmm}} = {today:MMMM}\n{{yy}} = {today:yy}\n{{yyyy}} = {today:yyyy}",
                "Operators", MessageBoxButtons.OK, MessageBoxIcon.Information );
        }

        private void LinkLabel5_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            System.Diagnostics.Process.Start( linkLabel5.Text );
        }

        private void LinkLabel3_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            System.Diagnostics.Process.Start( linkLabel3.Text );
        }

        private void LinkLabel4_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            System.Diagnostics.Process.Start( linkLabel4.Text );
        }

        private readonly Regex _highlightRegex = new Regex( "{d}|{dd}|{mm}|{mmm}|{mmmm}|{yy}|{yyyy}" ); // {(./?)}
        private void HighlightQueryBox() {
            var currIndex = queryRTBox.SelectionStart;

            //Recolor back to normal
            queryRTBox.SelectAll();
            queryRTBox.SelectionColor = Color.Teal;
            queryRTBox.SelectionFont = new Font( FontFamily.GenericSansSerif, 8.0F, FontStyle.Regular );

            MatchCollection matches = _highlightRegex.Matches( queryRTBox.Text );
            //Highlight
            if ( matches.Count > 0 ) {
                foreach ( Match match in matches ) {
                    queryRTBox.Select( match.Index, match.Length );
                    queryRTBox.SelectionColor = Color.Blue;
                    queryRTBox.SelectionFont = new Font( FontFamily.GenericSansSerif, 8.0F, FontStyle.Bold );
                }
            }

            queryRTBox.Select( currIndex, 0 );
            queryRTBox.DeselectAll();
        }
        private void QueryRTBox_TextChanged(object sender, EventArgs e) {
            if ( queryRTBox.Text.Contains( "{" ) )
                HighlightQueryBox();
        }
    }
}
