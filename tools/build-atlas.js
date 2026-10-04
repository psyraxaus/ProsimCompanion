// Builds the offline atlas (#153) from Natural Earth + GeoNames into one gzipped JSON.
// Sources (in this folder): countries.geojson (NE 110m admin_0), marine50.geojson (NE 50m marine
// polys), regions50.geojson (NE 50m geography regions), cities15000.txt (GeoNames).
// Output: src/ProsimCompanion.Core/Geo/Data/atlas.json.gz — a fixed path from this script's
// folder, never an argument (a command-line path into a file write is a path-traversal alert).
const fs = require("fs");
const path = require("path");
const zlib = require("zlib");
const out = path.join(__dirname, "..", "src", "ProsimCompanion.Core", "Geo", "Data", "atlas.json.gz");
const L = (f) => JSON.parse(fs.readFileSync(f, "utf8"));
const r3 = (v) => Math.round(v * 1000) / 1000;

// Rings as flat [lon,lat,lon,lat,...]; polygon = [outer, hole, hole...]; shape = polygon[]
function shape(geom) {
  const polys = geom.type === "Polygon" ? [geom.coordinates] : geom.type === "MultiPolygon" ? geom.coordinates : [];
  return polys.map((poly) => poly.map((ring) => {
    const flat = [];
    let prev = null;
    for (const [lon, lat] of ring) {
      const p = [r3(lon), r3(lat)];
      if (prev && prev[0] === p[0] && prev[1] === p[1]) continue;
      flat.push(p[0], p[1]);
      prev = p;
    }
    return flat;
  }).filter((f) => f.length >= 8));
}

const countries = L("countries.geojson").features.map((f) => ({
  n: f.properties.NAME_EN || f.properties.NAME,
  a2: (f.properties.ISO_A2_EH && f.properties.ISO_A2_EH !== "-99") ? f.properties.ISO_A2_EH : (f.properties.ISO_A2 !== "-99" ? f.properties.ISO_A2 : ""),
  sub: f.properties.SUBREGION || "",
  p: shape(f.geometry),
})).filter((c) => c.p.length);

const seas = L("marine50.geojson").features
  .filter((f) => f.properties.name && !/River/i.test(f.properties.name))
  .map((f) => ({ n: titleCase(f.properties.name), p: shape(f.geometry) }))
  .filter((c) => c.p.length);

const KEEP = new Set(["Range/mtn", "Desert", "Plateau", "Plain", "Basin", "Pen/cape", "Peninsula", "Island",
  "Delta", "Valley", "Geoarea", "Lowland", "Gorge", "Lake", "Wetlands", "Isthmus", "Tundra"]);
const regions = L("regions50.geojson").features
  .filter((f) => KEEP.has(f.properties.FEATURECLA) && (f.properties.NAME_EN || f.properties.NAME))
  .map((f) => ({ n: f.properties.NAME_EN || f.properties.NAME, k: f.properties.FEATURECLA, p: shape(f.geometry) }))
  .filter((c) => c.p.length);

// GeoNames columns: 1 name, 2 asciiname, 4 lat, 5 lon, 7 featurecode, 8 country, 14 population
const towns = fs.readFileSync("cities15000.txt", "utf8").split("\n").filter(Boolean).map((l) => l.split("\t"))
  .filter((c) => +c[14] >= 25000 || c[7] === "PPLC" || c[7] === "PPLA")
  .map((c) => [c[1], c[8], r3(+c[4]), r3(+c[5]), +c[14], c[7] === "PPLC" ? 2 : c[7] === "PPLA" ? 1 : 0]);

function titleCase(s) {
  return s === s.toUpperCase() ? s.toLowerCase().replace(/\b\w/g, (m) => m.toUpperCase()) : s;
}

const atlas = {
  about: "Countries: Natural Earth 1:110m admin_0 (public domain). Seas and natural regions: Natural Earth 1:50m (public domain). Towns: GeoNames cities15000 (CC BY 4.0, https://www.geonames.org), population >= 25 000 plus capitals and first-order seats.",
  countries, seas, regions, towns,
};
const json = JSON.stringify(atlas);
fs.writeFileSync(out, zlib.gzipSync(Buffer.from(json), { level: 9 }));
console.log(`countries ${countries.length} seas ${seas.length} regions ${regions.length} towns ${towns.length} json ${(json.length / 1e6).toFixed(2)} MB gz ${(fs.statSync(out).size / 1e6).toFixed(2)} MB`);
