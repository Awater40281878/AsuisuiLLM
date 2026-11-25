using System.IO.Compression;
using System.Security;
using System.Text;

public sealed class SpreadsheetService
{
	public string Create(string title, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
	{
		if (headers.Count == 0) throw new ArgumentException("表格至少需要一個欄位。", nameof(headers));
		if (rows.Any(row => row.Count != headers.Count)) throw new ArgumentException("每筆資料的欄位數必須一致。", nameof(rows));
		string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Asuisui AI", "表格");
		Directory.CreateDirectory(directory);
		string safeTitle = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
		if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = "資料表";
		string path = Path.Combine(directory, $"{safeTitle}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
		using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
		using var archive = new ZipArchive(file, ZipArchiveMode.Create);
		Write(archive, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
		Write(archive, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
		Write(archive, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"資料\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
		Write(archive, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
		var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
		AddRow(xml, 1, headers);
		for (int i = 0; i < rows.Count; i++) AddRow(xml, i + 2, rows[i]);
		xml.Append("</sheetData></worksheet>");
		Write(archive, "xl/worksheets/sheet1.xml", xml.ToString());
		return path;
	}

	private static void AddRow(StringBuilder xml, int rowIndex, IReadOnlyList<string> cells)
	{
		xml.Append($"<row r=\"{rowIndex}\">");
		for (int column = 0; column < cells.Count; column++)
		{
			string reference = ColumnName(column + 1) + rowIndex;
			xml.Append($"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{SecurityElement.Escape(cells[column] ?? string.Empty)}</t></is></c>");
		}
		xml.Append("</row>");
	}

	private static string ColumnName(int number)
	{
		var name = new StringBuilder();
		while (number > 0) { number--; name.Insert(0, (char)('A' + number % 26)); number /= 26; }
		return name.ToString();
	}

	private static void Write(ZipArchive archive, string name, string content)
	{
		var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
		using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
		writer.Write(content);
	}
}
