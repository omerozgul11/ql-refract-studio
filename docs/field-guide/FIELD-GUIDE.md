# Seismic refraction field guide

## 1. Before you start

This guide takes you from the equipment case to a finished seismic refraction report.
It assumes no previous experience. Read it once in the office, then take the
checklists (section 11) to the field.

### What seismic refraction measures

When a hammer hits the ground, waves travel down until they reach a faster layer,
such as dense gravel, the water table (for P waves) or bedrock. There they bend and
run along the top of the faster layer, sending energy back up to the surface. Far
from the shot, these refracted waves overtake the waves travelling directly along
the surface.

A line of geophones records the **first arrival** (the first break) at every
position. Refraction Studio picks these times and turns them into the velocity of
each layer and the depth to each refractor. It can also compute a gradational
velocity section (tomography).

- **P waves** (compressional) give the P-wave velocity (Vp). Use them for depth to
  bedrock, rippability, the water table and layer thickness.
- **SH waves** (horizontal shear) give the shear-wave velocity (Vs). Use them for
  stiffness, Vs30 site class and, with P waves, Poisson's ratio and the elastic
  moduli.

{{diagram:rays}}

> **You need three things right in the field:** well-planted geophones at measured
> positions, a sharp trigger at the moment of every hit, and shots at both ends of
> the line. Everything else can be checked and corrected in Refraction Studio.

> **Know the limits.** Refraction assumes that velocity increases with depth. A
> slower layer under a faster one, or a very thin layer, can be missed. Tomography
> and a second line at a different spacing help; borehole logs remain the best
> check.

### The equipment list

| Item | What to bring | Notes |
|---|---|---|
| Seismograph | Recording unit or master unit, its cables and the laptop with the recording software | 24 channels or more; 48 gives better tomography |
| Line cable | Cable with a take-out (or digitizer shell) for every channel | The take-out spacing limits the geophone spacing |
| Geophones, P line | One **vertical** geophone per channel, 10–14 Hz or 4.5 Hz, with spikes | Plus two spares |
| Geophones, SH line | One **horizontal** geophone per channel, 10–14 Hz or 4.5 Hz, with spikes and a bubble level | Plus two spares |
| Trigger | Trigger switch on the hammer handle, or a trigger geophone next to the plate | Starts every recording |
| Reference geophone (optional) | An extra geophone next to the shot, on a spare channel | Lets Refraction Studio correct trigger timing errors |
| P source | Sledgehammer, **5–10 kg (10–20 lb)**, and a nylon or aluminium strike plate | A weight drop for long lines |
| SH source | Shear beam: a hardwood beam, about 2–3 m (7–10 ft) long, with steel end plates | Held down by a vehicle wheel or about 200 kg (440 lb) of weights |
| Measuring tape | 100 m (330 ft) tape, plus pegs | Lay it along the whole line |
| Markers | Survey flags, spray paint or pegs | Mark every geophone and shot position |
| Elevations | Level and staff, total station or GNSS/GPS receiver | Needed whenever the ground is not flat |
| Power | Charged batteries for the seismograph and laptop | Check them the day before |
| Field sheet | Printed field sheet (section 11) and a pencil | Write down every file |
| Safety | Ear defenders, safety glasses, gloves, safety boots, high-visibility vest | The hammer operator needs all of them |

### Check everything in the office

1. Charge every battery and the laptop.
2. Connect the seismograph to the laptop and start the recording software. Check
   that every channel answers and that the trigger works (tap the trigger switch or
   the trigger geophone and watch for a recording to start).
3. Check that the software saves records as **SEG-2** (`.sg2`, `.seg2`, `.dat`) or
   **SEG-Y** (`.sgy`, `.segy`); Refraction Studio reads both.
4. Install Refraction Studio on the computer you will use for the analysis
   (section 10). It can be the same laptop.

## 2. Plan the line

### Pick the place

- Choose a **straight line** across the feature you want to see, for example across
  a buried valley rather than along it.
- Prefer open ground. Slopes are fine if you measure the elevations (section 8).
- Keep away from **noise**: engines, generators, pumps, busy roads and people
  walking near the geophones. If you cannot avoid traffic, record between
  vehicles.
- Avoid lines that run along buried pipes, walls or foundations; they carry waves
  of their own.

### How deep can you see?

The depth you can see depends on the **length of the geophone line** (the spread).
A good rule of thumb: the spread should be **three to five times the depth you need**.
Off-end shots (see below) add depth without adding geophones.

| Wanted depth | Geophone spacing | Spread (24 channels) | Spread (48 channels) | Source |
|---|---|---|---|---|
| 3–8 m (10–25 ft) | 1–2 m (3–6.5 ft) | 23–46 m (75–150 ft) | 47–94 m (155–310 ft) | 5 kg (10 lb) hammer |
| 8–20 m (25–65 ft) | 3 m (10 ft) | 69 m (225 ft) | 141 m (460 ft) | 8–10 kg (16–20 lb) hammer, several hits per shot |
| 20–40 m (65–130 ft) | 5 m (16 ft) | 115 m (380 ft) | 235 m (770 ft) | Weight drop, or many stacked hammer hits |

- **Closer spacing** shows thin near-surface layers and short changes along the line.
- **Vs30** needs the SH model to reach 30 m (100 ft). That usually needs a long SH
  spread with off-end shots.

### Where the shots go

Record shots at several positions along the same line. Each shot gives one
travel-time curve; together they give the reverse coverage that the layered methods
need and the ray coverage that tomography needs.

| Shot | Position | Why |
|---|---|---|
| End shots | Half a spacing beyond the first and last geophone | Forward and reverse times for the delay-time and plus–minus methods |
| Off-end shots | Beyond both ends, at about one-quarter of the spread length | Deeper refractor coverage under the end geophones |
| Interior shots | Every 6 to 12 geophones along the line | Detail along the line and ray coverage for tomography |

**Minimum:** both end shots and one centre shot. **Recommended:** seven shots or more,
as in the sample project: two off-end, two end and three interior shots on a 48-channel
line at 3 m spacing.

{{diagram:spread}}

## 3. Lay out the line

1. **Stretch the measuring tape** along the line and fix both ends. Decide which end
   is **channel 1** and write it on the field sheet.
2. **Mark the positions.** Put a flag or paint mark at every geophone position (for
   example 0, 3, 6 … 141 m) and at every shot position.
3. **Lay out the line cable** along the tape, with channel 1 at the first mark. Keep
   it flat on the ground; a cable moving in the wind adds noise.
4. **Plant the geophones.** Scrape away grass and loose topsoil, then push each
   geophone in by hand until its base sits on the ground. Tap the spike in if needed,
   but never hit the geophone case.
5. **P line: vertical geophones upright,** within the tilt limit on the geophone's data
   sheet (typically 10–20°). A tilted or loose geophone gives a weak, noisy trace.
6. **SH line: horizontal geophones level and turned the same way,** with their sensitive
   axis **across the line** (perpendicular to it) and parallel to the shear beam. Use
   the bubble level. A geophone turned the wrong way records the wrong polarity.
7. **Connect each geophone** to its take-out. Keep the connectors clean and dry.

{{diagram:orientation}}

> **On pavement or concrete** you cannot use spikes. Use geophone base plates or
> tripod stands, or sit the geophones on small sandbags. Each geophone must still be
> firmly in contact with the ground.

## 4. Set up the seismograph

1. **Connect** the line cable, the trigger and the power to the seismograph, then the
   seismograph to the laptop.
2. **Start the recording software** and check that every channel is found. A missing
   channel means a loose connector or a faulty take-out.
3. **Choose a new folder** for each site or line. Every record is saved there.
4. **Enter the positions** if the software allows it: the X position of every channel,
   and the shot position before every shot. Elevations go in as Z. Refraction Studio
   reads them from SEG-2 and SEG-Y headers; otherwise you enter the layout in the
   Geometry page.

### Recording settings

| Setting | P line | SH line | Why |
|---|---|---|---|
| Sample interval | 0.125–0.25 ms (4,000–8,000 samples per second) | 0.25–0.5 ms | First breaks must be timed to a fraction of a millisecond |
| Record length | 0.25–0.5 s | 0.5–1 s | Long enough for the first arrival at the farthest geophone, plus margin |
| Delay / pre-trigger | 0, or a pre-trigger saved in the file | 0, or a pre-trigger saved in the file | Refraction Studio applies the delay written in the file header |
| Filters | Off (or a low-cut below 10 Hz) | Off | Filters in the field shift first breaks; filter later in Refraction Studio |
| Gain | The same on every channel | The same on every channel | Fixed gains keep amplitudes comparable |
| Stacking | 3–10 hits per shot point | 5–10 hits per side | More hits raise the signal above the noise |

> **Record length rule:** the farthest offset divided by the slowest velocity, plus
> 50 %. For a 180 m (590 ft) offset and a slowest P velocity of 400 m/s, that is
> 0.45 s plus margin: record 0.5 s.

### Noise check

Before the first shot, watch the noise display with nobody moving. Every channel
should show a small, steady signal. A flat channel is not connected; a channel with
large jumps is loose, in the wind or next to someone walking.

## 5. Record the P shots

1. **Place the strike plate** on the shot mark and press it firmly into the ground.
   Put the trigger geophone right next to the plate, or check the hammer switch.
2. **Optional reference geophone:** plant a geophone about 0.5–1 m (2–3 ft) from the
   plate on a spare channel, and note the channel number. Refraction Studio uses it to
   correct trigger timing errors (channel role **Reference** in Geometry).
3. Make sure nobody is walking near the line. The hammer operator wears ear defenders,
   safety glasses and gloves; everyone else stands back.
4. **Arm the recording** and hit the centre of the plate squarely with a full swing.
5. **Repeat and stack** 3–10 hits at the same point (in the seismograph, or keep the
   records separate and stack them in Refraction Studio at import).
6. **Check the record** (below), then write the file name, shot position and notes on
   the field sheet.
7. **Move to the next shot point** and update the shot position.

### What a good record looks like

- **Good:** the first breaks form a clear line across the record, earliest next to the
  shot and later at every farther geophone, often with a change of slope where the
  refracted wave overtakes the direct wave.
- **Dead channel:** one flat trace. Check the geophone and its connector.
- **Noisy channel:** one trace full of jitter. Re-plant the geophone and check for wind.
- **Clipped:** flat-topped waveforms next to the shot. That is normal on the nearest
  one or two geophones; on more, hit more softly or lower the gain.
- **Weak far traces:** first breaks are lost in the noise at the far end. Stack more
  hits, use a heavier source or add off-end shots.

## 6. Record the SH shots

SH waves are generated by striking a beam sideways. Hitting it from the left and then
from the right reverses the shear wave but not the P wave, which lets Refraction Studio
separate them.

1. **Lay the shear beam** on the shot mark, **across the line** (perpendicular to it),
   parallel to the sensitive axis of the horizontal geophones.
2. **Load the beam** so it cannot slide: park a vehicle wheel on it, or stack about
   200 kg (440 lb) of weights. Good contact with the ground matters more than the
   weight of the hammer.
3. **Hit one end** of the beam horizontally, along its length, and record. Stack 5–10
   hits. Name the file with **L** (for example `shot_003_L.sg2`).
4. **Hit the other end** the same number of times and record a separate file named
   with **R** (`shot_003_R.sg2`). Do not stack left and right hits together.
5. **Check the pair:** the shear-wave arrivals on the L and R records must have opposite
   polarity. If they look the same, the geophones or the hits are aligned wrongly.
6. Repeat at every SH shot position, using the same positions as the P line when you
   want Poisson's ratio and the elastic moduli.

{{diagram:shbeam}}

> **In Refraction Studio**, import the L and R records of a line together and choose
> **Opposite SH impacts** under *Records at the same position*. Each pair is combined
> as (left − right)/2: shear energy adds and P energy cancels.

## 7. Recording log and file names

- Use one folder per line and wave type (for example `Line1_P` and `Line1_SH`).
- Name files by shot number, with L/R for SH: `shot_001.sg2`, `shot_001_L.sg2`.
- Write every file on the field sheet with its shot position, the number of hits and
  anything unusual (traffic, wind, a re-planted geophone).

## 8. Sloping ground and elevations

Refraction depths are measured from the ground surface, so elevations matter whenever
the ground is not flat.

1. Lay the tape **along the ground** and mark positions along the tape as usual.
2. Measure the **elevation of every geophone and every shot point** with a level and
   staff, a total station or a GNSS/GPS receiver. Write them on the field sheet.
3. Enter them as **Z** in the recording software if it allows it, so they are saved in
   every file.
4. Otherwise, in Refraction Studio open **Geometry → Ground profile** and paste or
   import the surveyed points (position and elevation) as CSV. Geophone and shot
   elevations are interpolated from this profile.

## 9. Before you leave the site

- Copy the data folders to a USB stick **and** keep them on the laptop: two copies.
- Check the field sheet: every file has a shot position, the spacing, the channel 1
  end, the wave type and notes.
- Photograph the line from both ends with the geophones in place.
- Note the GNSS/GPS coordinates of both ends of the line.
- Count the geophones before you pack them.

## 10. Install Refraction Studio and analyse the data

### Install

1. Run **RefractionStudio-1.0.0-win-x64-setup.exe** on a computer running Windows 10
   (version 1809 or later) or Windows 11, 64-bit.
2. Windows may show **Windows protected your PC** because the installer is not yet
   code-signed: choose **More info → Run anyway**.
3. Follow the installer and start Refraction Studio. It is the full version: there is no
   licence key, no activation and no internet connection needed.
4. Next time, start it from **Start → Refraction Studio** or the desktop shortcut.

Choose **Explore the sample** on the start page once: it is a complete P and SH survey
that shows every step below.

### Analyse your survey

The workflow on the left of the window lists the steps in order, with the status of
each one.

1. **Project.** Enter the project, client and site; they appear on the report.
2. **Import.** Choose the files or the folder of one line. Check the validation
   findings, choose the wave type (P or SH) and, for SH, **Opposite SH impacts**, then
   import.
3. **Geometry.** Compare the positions with the field sheet. Set the role of the
   reference geophone, if you used one, and enter the ground profile.
4. **Waveforms and Processing.** Look through the shots. Remove the DC offset and, if
   surface waves hide the first breaks, add a causal high-pass filter.
5. **P picks and S picks.** Press **Pick automatically**, accept the high-confidence
   picks and review the rest: click a trace to place or move a pick.
6. **Quality control.** Work through the findings; each one says what to check.
7. **Velocity model.** Press **Suggest layer assignment**, check the crossovers, apply
   the delay-time method, then run tomography for a gradational model.
8. **Engineering.** Vs30, average velocities, Poisson's ratio and the elastic moduli.
9. **Results and Report.** Export the tables and figures, then generate the PDF report.
   Save the project with **Ctrl+S**.

![Velocity model page: travel times, the layered model and tomography](images/velocity-model.png)

The user guide (in the software under **Help**, F1, and as a PDF in this package)
explains every setting. The training video shows the whole workflow in about six
minutes.

## 11. Field checklists and field sheet

### Before you leave the office

- Seismograph, laptop with recording software, cables, charged batteries
- Line cable and spare connectors
- Vertical geophones (P) and horizontal geophones (SH), one per channel plus two spares
- Trigger switch or trigger geophone; optional reference geophone
- Sledgehammer and strike plate; shear beam and its load (vehicle or weights)
- Measuring tape, flags, pegs, small hammer
- Level and staff, total station or GNSS receiver
- Printed field sheets, pencil, safety equipment
- Recording and trigger tested in the office

### On site, before the first shot

- Line straight, geophone and shot positions marked and written down
- Channel 1 end decided and noted
- P geophones upright and firm; SH geophones level, across the line, all turned the same way
- Every channel found by the recording software; trigger tested
- Settings: 0.125–0.25 ms (P) or 0.25–0.5 ms (SH) sampling, record length for the
  farthest offset, no field filters, the same gain on every channel
- Noise checked: no dead or noisy channels

### Field sheet

| File | Line / wave | Shot position (m) | Hits | Channel 1 at (m) | Spacing (m) | Notes (noise, weather, elevations) |
|---|---|---|---|---|---|---|
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |
| &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; | &nbsp; |

## 12. Troubleshooting

| Problem | Likely cause | What to do |
|---|---|---|
| A channel is missing in the recording software | Loose connector or faulty take-out | Re-seat the connectors from channel 1 outwards |
| Nothing records when the hammer hits | Trigger cable loose, or trigger geophone too far from the plate | Re-plug the trigger; put the trigger geophone right next to the plate |
| Recording starts by itself | Trigger disturbed by footsteps or wind | Keep people away from the trigger geophone; raise the trigger threshold |
| One trace is flat | Geophone unplugged or broken | Re-plug or swap the geophone |
| First breaks are lost at the far end | Not enough energy | Stack more hits, use a heavier source, shorten the spread or add off-end shots |
| First breaks jump earlier or later on a whole shot | Trigger timing error | Use a reference geophone; correct the shot time in Processing → Shot time correction |
| L and R SH records look the same | Geophones not turned the same way, or the beam hit on the same side | Check the geophone orientation and the hit direction; re-record |
| Refraction Studio warns that the sample rates differ | Settings changed between shots | Keep one sample interval per line; import different rates as separate lines |
| The layered model is "not available" | Too few picks assigned to a refractor | Press Suggest layer assignment, then check the crossovers on the travel-time plot |
| Vs30 is not available | The SH model does not reach 30 m (100 ft) | Longer SH spread or off-end shots; extrapolation is possible but is labelled |

**Help:** email support@quakelogic.net or call QuakeLogic on +1 (916) 899-0391.
Email response time: 3 business days.
