﻿using System;
using Pulsus.Gameplay;
using Pulsus.Input;
using SDL2;

namespace Pulsus
{
	public class GameplayScene : Scene
	{
		InputMapper inputMapper;
		Chart chart;
		EventPlayerGraph playerGraph;
		Skin skin;
		BGM bgmPlayer;
		BMSJudge judge;
		Player player;
		Loader loader;

		public GameplayScene(Game game, string inputPath) : base(game)
		{
			Settings settings = SettingsManager.instance;
			double judgeOffset = settings.gameplay.judgeOffset / 1000.0;

			Log.Info("Loading chart: " + inputPath);
			chart = Chart.Load(inputPath);

			Log.Info("Generating events...");
			chart.GenerateEvents();

			Log.Info("Initializing players...");

			judge = new BMSJudge(chart);
			skin = new Skin(chart, renderer, judge);
			judge.OnNoteJudged += skin.OnNoteJudged;

			bgmPlayer = new BGM(chart, audio);
			player = new Player(chart, audio, judge, skin);
			loader = new Loader(chart, audio);

			// add players to graph
			playerGraph = new EventPlayerGraph();
			playerGraph.Add(judge);
			playerGraph.Add(player);
			playerGraph.Add(bgmPlayer);
			playerGraph.Add(skin);
			playerGraph.Add(loader);

			// adjust offsets

			double startTime = 0;
			if (settings.startMeasure > 0)
				startTime = chart.GetTimeFromPulse(chart.measurePositions[settings.startMeasure].Item2);
			else
			{
				double firstNoteTimestamp = 0.0;
				foreach (Event @event in chart.eventList)
				{
					NoteEvent noteEvent = @event as NoteEvent;
					if (noteEvent == null)
						continue;

					firstNoteTimestamp = noteEvent.timestamp;
					break;
				}

				// give player some time to react to the first note
				if (firstNoteTimestamp < skin.baseScrollTime)
					startTime -= skin.baseScrollTime;
			}

			playerGraph.SetStartPosition(startTime);

			player.autoplay = settings.gameplay.autoplay;
			if (!player.autoplay)
				judge.startTime += judgeOffset;

			// load sound and bga objects

			System.Diagnostics.Stopwatch loadTimer = System.Diagnostics.Stopwatch.StartNew();
			if (settings.songPreload)
				loader.PreloadAll(true, !settings.gameplay.disableBGA);
			else
			{
				// preload all BGA objects
				if (!settings.gameplay.disableBGA)
					loader.PreloadAll(false, true);

				loader.Preload(); // preload few seconds ahead
			}
			loadTimer.Stop();
			Log.Info("Preload finished in " + loadTimer.Elapsed.TotalSeconds.ToString() + "s");

			// bind input
			inputMapper = new InputMapper(game.inputManager);

			InputLayout keyLayout = null;
			InputLayout generalLayout = settings.input.layouts["general"];
			int laneCount = chart.playerChannels;
			if (laneCount == 6)
				settings.input.layouts.TryGetValue(settings.input.default5k, out keyLayout);
			if (laneCount == 8 || keyLayout == null)
				settings.input.layouts.TryGetValue(settings.input.default7k, out keyLayout);
			if (laneCount == 9 || keyLayout == null)
				settings.input.layouts.TryGetValue(settings.input.default9k, out keyLayout);

			InputLayout layout = null;
			if (keyLayout != null)
			{
				layout = new InputLayout();
				layout.keys = new System.Collections.Generic.Dictionary<string, string[]>(keyLayout.keys);
				foreach (var key in generalLayout.keys.Keys)
				{
					// override non-present keys
					if (!layout.keys.ContainsKey(key) || layout.keys[key].Length == 0)
						layout.keys[key] = generalLayout.keys[key];
				}
			}
			else
				layout = generalLayout;

			// ensure that exit is always bound to somewhere
			if (layout.keys["exit"].Length == 0)
				layout.keys["exit"] = new string[] { new InputKey(SDL.SDL_Scancode.SDL_SCANCODE_ESCAPE).Name };

			BindInputLayout(layout);

			// start all the players
			Log.Info("Starting player graph");
			playerGraph.Start();
		}

		public override void Dispose()
		{
			audio.StopAll();

			if (skin != null)
				skin.Dispose();
			if (bgmPlayer != null)
				bgmPlayer.Dispose();
			if (judge != null)
				judge.Dispose();
			if (player != null)
				player.Dispose();
			if (loader != null)
				loader.Dispose();
			if (chart != null)
				chart.Dispose();
		}

		private void BindInputLayout(InputLayout layout)
		{
			Settings settings = SettingsManager.instance;

			BindKey(layout.GetInputs("exit"), InputAction.OnPressed(() =>
			{
				Close();
			}));

			const double scrollStep = 0.001;

			BindKey(layout.GetInputs("scrollSpeedInc"), InputAction.OnDown(() =>
			{
				skin.baseScrollTime += scrollStep;
				if (skin.baseScrollTime >= 10.0)
					skin.baseScrollTime = 10.0;

				settings.gameplay.scrollTime = skin.baseScrollTime;
			}));

			BindKey(layout.GetInputs("scrollSpeedDec"), InputAction.OnDown(() =>
			{
				skin.baseScrollTime -= scrollStep;
				if (skin.baseScrollTime < scrollStep)
					skin.baseScrollTime = scrollStep;

				settings.gameplay.scrollTime = skin.baseScrollTime;
			}));

			if (!settings.gameplay.autoplay)
			{
				int laneOffset;
				if (chart.hasTurntable)
				{
					laneOffset = 0;
					BindLaneKey(layout.GetInputs("turntable"), 0);
				}
				else
					laneOffset = -1;

				int keyCount = 9;
				for (int key = 1; key <= keyCount; key++)
					BindLaneKey(layout.GetKeyInputs(key), key + laneOffset);
			}
		}

		private void BindKey(InputType[] inputs, InputAction inputAction)
		{
			foreach (InputType input in inputs)
			{
				if (input is InputKey)
					inputMapper.MapInput((input as InputKey).scancode, inputAction);
				else if (input is InputJoystick)
					inputMapper.MapInput((input as InputJoystick).button, inputAction);
				else
					throw new ApplicationException("Unable to bind unknown type of InputType");
			}
		}

		private void BindLaneKey(InputType[] inputs, int lane)
		{
			foreach (InputType input in inputs)
			{
				if (input is InputKey)
					BindLaneKey((input as InputKey).scancode, lane);
				else if (input is InputJoystick)
					BindLaneKey((input as InputJoystick).button, lane);
				else
					throw new ApplicationException("Unable to bind unknown type of InputType");
			}
		}

		private void BindLaneKey(JoyInput button, int lane)
		{
			inputMapper.MapInput(button, InputAction.OnPressedReleased(
				() => player.PlayerPressKey(lane),
				() => player.PlayerReleaseKey(lane)
			));
		}

		private void BindLaneKey(SDL.SDL_Scancode scancode, int lane)
		{
			inputMapper.MapInput(scancode, InputAction.OnPressedReleased(
				() => player.PlayerPressKey(lane),
				() => player.PlayerReleaseKey(lane)
			));
		}

		public override void Update(double deltaTime)
		{
			inputMapper.Update(deltaTime);
			playerGraph.Update(deltaTime);
		}

		public override void Draw(double deltaTime)
		{
			skin.Render(deltaTime);
		}
	}
}
