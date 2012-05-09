﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.IO;
using System.Configuration;
using System.Text.RegularExpressions;

namespace deploy.Models {
	public class Builder {


		string _id;
		string _appdir;
		Dictionary<string, string> _config;
		string _logfile;
		string _sourcedir;

		public Builder(string id) {
			_id = id;
		}


		public void Build() {
			lock(AppLock.Get(_id)) {
				Init();

				FileDB.AppState(_id, "building");
				try {					
					GitUpdate();
					NugetRefresh();
					Msbuild();
					Deploy();

					Log("-> build completed");
					FileDB.AppState(_id, "idle");
				} catch(Exception e) {
					Log("ERROR: " + e.ToString());
					Log("-> build failed!");
					FileDB.AppState(_id, "failed");
					throw;
				}

			}
		}

		private void Init() {
			var state = FileDB.AppState(_id);
			if(state.Item2 != "idle" && state.Item2 != "failed") throw new Exception("Can't build: current state is " + state.Item2);

			_appdir = FileDB.AppDir(_id);
			_config = FileDB.AppConfig(_id);
			_logfile = Path.Combine(_appdir, "log.txt");
			_sourcedir = Path.Combine(_appdir, "source");

			if(File.Exists(_logfile)) File.Delete(_logfile); // clear log
		}

		private void GitUpdate() {
			var giturl = _config["git"];
			if(string.IsNullOrEmpty(giturl)) throw new Exception("git missing from config");

			if(!Directory.Exists(_sourcedir)) {
				Directory.CreateDirectory(_sourcedir);
				Log("-> doing git clone");
				new Cmd("git clone " + giturl + " source", runFrom: _appdir, logPath: _logfile).Run().EnsureCode(0);
			} else {
				Log("-> doing git pull");
				new Cmd("git pull " + giturl, runFrom: _sourcedir, logPath: _logfile).Run().EnsureCode(0);
			}
		}

		private void NugetRefresh() {
			Log("-> doing nuget refresh");
			new Cmd("echo off && for /r . %f in (packages.config) do if exist %f echo found %f && nuget i \"%f\" -o packages", runFrom: _sourcedir, logPath: _logfile)
				.Run().EnsureCode(0);
		}

		private void Msbuild() {
			var msbuild = ConfigurationManager.AppSettings["msbuild"];

			Log("-> building with " + msbuild);
			new Cmd("\"" + msbuild + "\"", runFrom: _sourcedir, logPath: _logfile)
				.Run().EnsureCode(0);
		}

		private void Deploy() {
			string deploy_base, deploy_to, deploy_ignore = null;

			_config.TryGetValue("deploy_base", out deploy_base);
			_config.TryGetValue("deploy_to", out deploy_to);
			_config.TryGetValue("deploy_ignore", out deploy_ignore);

			if(string.IsNullOrWhiteSpace(deploy_to)) throw new Exception("deploy_to not specified in config");

			Log(" -> deploying to " + deploy_to);

			var source = string.IsNullOrEmpty(deploy_base) ? _sourcedir : Path.Combine(_sourcedir, deploy_base);

			if(!Directory.Exists(deploy_to)) Directory.CreateDirectory(deploy_to);

			List<string> simple;
			List<string> paths;
			GetIgnore(source, deploy_to, deploy_ignore, out simple, out paths);

			var xf = new List<string>(simple);
			var xd = new List<string>(simple);
			xd.AddRange(paths);

			var xf_arg = xf.Count > 0 ? " /xf " + string.Join(" ", xf) : null;
			var xd_arg = xd.Count > 0 ? " /xd " + string.Join(" ", xd) : null;

			new Cmd("\"robocopy . \"" + deploy_to + "\" /s /purge /nfl /ndl " + xf_arg + xd_arg + "\"", runFrom: source, logPath: _logfile)
				.Run();
		}

		private void GetIgnore(string source, string dest, string ignore_str, out List<string> simple, out List<string> paths) {
			simple = new List<string>();
			paths = new List<string>();

			if(string.IsNullOrWhiteSpace(ignore_str)) return; // nothing to ignore

			var ignore = Regex.Split(ignore_str, @"(?<!\\)\s+"); // split on spaces, unless they're escaped with \

			var path_segments = new List<string>();
			foreach(var i in ignore) {
				var part = i.Replace(@"\ ", " ");
				if(part.Contains("\\")) path_segments.Add(part);
				else simple.Add(QuoteSpacesInPath(part));
			}

			if(path_segments.Count > 0) {
				// have to manually look for directories that match the path segment
				var source_paths = DirectoriesIn(source);
				var dest_paths = DirectoriesIn(dest);

				foreach(var seg in path_segments) {
					paths.AddRange(MatchingDirectories(seg, source_paths).Select(s => QuoteSpacesInPath(s)));
					paths.AddRange(MatchingDirectories(seg, dest_paths).Select(s => QuoteSpacesInPath(s)));
				}
			}
		}

		private static IEnumerable<string> MatchingDirectories(string segment, string[] directories) {
			var matches = directories
				.Select(s => new { path = s, index = s.IndexOf(segment, StringComparison.OrdinalIgnoreCase) })
				.Where(p => p.index != -1)
				.OrderBy(s => s.path.Length);

			if(matches.Count() == 0) return new string[0];

			List<string> filtered = new List<string>();
			var seenPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach(var match in matches) {
				var prefix = match.path.Substring(0, match.index);
				if(seenPrefixes.Contains(prefix)) continue; // already added a parent
				filtered.Add(match.path.TrimEnd('\\'));
				seenPrefixes.Add(prefix);
			}

			return filtered;
		}

		private static string[] DirectoriesIn(string path) {
			return new Cmd("echo off && for /r %d in (.) do echo %~fd", runFrom: path).Run().EnsureCode(0).Output
					.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
		}

		private static string QuoteSpacesInPath(string path) {
			return Regex.Replace(path, @"^(\S*\s+)(\S*\s*)*", "\"$&\"");
		}

		private void Log(string message) {
			File.AppendAllText(_logfile, message + "\r\n");
		}
	}
}