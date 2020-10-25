﻿/*
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CS_Proxy.Proxy {
    internal class IPRange {
        private bool ParsingError = false;
        private readonly byte[] start = new byte[4] { 0, 0, 0, 0 };
        private readonly byte[] end = new byte[4] { 255, 255, 255, 255 };
        private int Length = 4; //ipv4 consists of 4 bytes

        public IPRange(string range) {
            range = range.Replace( " ", "" );

            if ( !range.Contains( "–" ) ) { //Single IP ex: '127.*.*.*'
                var parts = range.Split( new char[] { '.' }, StringSplitOptions.RemoveEmptyEntries );
                for ( var i = 0; i < parts.Length; ++i ) {
                    if ( !byte.TryParse( parts[i], out var b ) ) {
                        Console.WriteLine( "Error parsing dangerous IP {0} into bytes!", range );
                        ParsingError = true;
                    } else {
                        start[i] = b;
                        end[i] = b;
                    }
                }
            } else { //Range of: IP - IP ex: '127.0.0.1 - 127.0.128.255'
                var ranges = range.Split( new char[] { '–' }, StringSplitOptions.RemoveEmptyEntries );
                if ( ranges.Length == 2 ) {
                    //Determine which of ranges is smallest for proper comparison
                    var smallest = 0;
                    var largest = 1;
                    if ( long.TryParse( ranges[0].Replace( ".", "" ), out var range1 ) && long.TryParse( ranges[1].Replace( ".", "" ), out var range2 ) ) {
                        if ( range1 > range2 ) {
                            largest = 0;
                            smallest = 1;
                        }
                    }

                    //Store them
                    start = getIPBytes( ranges[smallest] );
                    end = getIPBytes( ranges[largest] );
                }
            }
        }

        public static byte[] getByteArray(string ip) {
            if ( ip.Contains( ":" ) )
                ip = ip.Substring( 0, ip.IndexOf( ":" ) );

            var bytes = new List<byte>();
            var parts = ip.Split( new char[] { '.' }, StringSplitOptions.RemoveEmptyEntries );
            for ( var i = 0; i < parts.Length; ++i ) {
                if ( byte.TryParse( parts[i], out var b ) )
                    bytes.Add( b );
            }

            return bytes.ToArray();
        }

        private byte[] getIPBytes(string ip) {
            var bytes = new byte[4];
            var parts = ip.Split( new char[] { '.' }, StringSplitOptions.RemoveEmptyEntries );
            for ( var i = 0; i < parts.Length; ++i ) {
                if ( i > 4 )
                    continue;

                if ( !byte.TryParse( parts[i], out var b ) ) {
                    Console.WriteLine( "Error parsing dangerous IP {0} into bytes!", ip );
                    ParsingError = true;
                } else
                    bytes[i] = b;
            }

            if ( !ParsingError )
                Length = parts.Length < Length ? parts.Length : Length;

            return bytes;
        }

        public bool isInRange(string ip) {
            if ( ParsingError )
                return false;

            var portIndex = ip.IndexOf( ":" );
            if ( portIndex > 0 )
                ip = ip.Substring( 0, portIndex );

            var b = getIPBytes( ip );
            return isInRange( b );
        }

        public bool isInRange(byte[] b) {
            if ( ParsingError )
                return false;

            var bytesNo = b.Length < Length ? b.Length : Length;
            for ( var i = 0; i < bytesNo; ++i ) {
                if ( b[i] >= start[i] && b[i] <= end[i] )
                    continue;
                else
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Filters Dangerous IP from file called dangerous_ip_ranges.txt
    /// </summary>
    public class ProxyFilter {
        private readonly List<IPRange> DangerousIPs = new List<IPRange>();
        public bool isInitialized { get; private set; }

        public ProxyFilter(string filterFile) {
            isInitialized = PopulateDangerousIPs( filterFile );
            if ( !isInitialized )
                Console.WriteLine( "Dangerous IP range filter could NOT be initialized." );
        }

        private bool isNumeric(string str) {
            return str.All( char.IsDigit );
        }

        private bool isNumeric(char c) {
            return char.IsDigit( c );
        }

        private bool PopulateDangerousIPs(string fileName) {
            var dir = Path.GetDirectoryName( System.Reflection.Assembly.GetExecutingAssembly().Location );
            var ipRangesFile = string.Concat( dir, "\\", fileName );

            if ( File.Exists( ipRangesFile ) ) {
                const string regExpr = @"((\d{1,3}\.(\d{1,3}(\.|\s)){0,3})(\–\s)(\d{1,3}\.(\d{1,3}(\.|\s)){0,3}))|(\d{1,3}\.(\d{1,3}(\.|\s)){0,3})"; //range of IPs or 1-3d. & (1-3d(.| ) {0 to 3x max})

                using ( var fs = new FileStream( ipRangesFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ) ) //IMP: Add exception checks
                using ( var sr = new StreamReader( fs, Encoding.UTF8 ) ) {
                    string line;
                    while ( (line = sr.ReadLine()) != null ) {
                        if ( line.Length == 0 || !isNumeric( line[0] ) )
                            continue;

                        Match match = Regex.Match( line, regExpr );
                        if ( match.Success ) {
                            var str = match.ToString();
                            DangerousIPs.Add( new IPRange( str ) );
                        }
                    }
                }

                return true;
            } else
                Console.WriteLine( "Filter file '{0}' could NOT be found!", fileName );

            return false;
        }

        public bool isDangerous(string proxy) {
            if ( !isInitialized || proxy.Length < 8 )
                return false;

            var buf = IPRange.getByteArray( proxy );
            if ( buf.Length <= 0 )
                return false;

            for ( var i = 0; i < DangerousIPs.Count; ++i ) {
                if ( DangerousIPs[i].isInRange( buf ) )
                    return true;
            }
            return false;
        }
    }
}
