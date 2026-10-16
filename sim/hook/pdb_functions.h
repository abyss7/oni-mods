// List of functions from a .pdb file: name, address relative to the module start, and length.
//
// File format (MSF 7.00): the file is split into blocks, data streams are assembled from blocks via a directory. Two kinds of streams are needed:
// DBI (number 3: the list of link modules, each with its own symbol stream, and the number of the section header stream)
// and the module symbol streams. A function is an S_GPROC32 or S_LPROC32 record or its _ID variant: it holds the
// section, the offset within the section, the length and the name ("SimBase::UpdateData", "AddGas"). The address is the offset
// plus the section start. Reading does not depend on the operating system.
#pragma once

#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include <algorithm>
#include <string>
#include <vector>

struct PdbFunction
{
	std::string name;
	uint32_t rva;
	uint32_t size;
};

// What ties the file to the library: the same is recorded in the library itself (RSDS record of the debug directory).
struct PdbIdentity
{
	uint8_t guid[16];
	uint32_t age;
};

namespace pdb_detail
{
	struct Reader
	{
		std::vector<uint8_t> file;
		uint32_t blockSize = 0;
		std::vector<uint32_t> streamSizes;
		std::vector<std::vector<uint32_t>> streamBlocks;

		template <class T> static T At(const std::vector<uint8_t>& data, size_t offset)
		{
			T value;
			memcpy(&value, data.data() + offset, sizeof(T));
			return value;
		}

		bool Open(const char* path, std::string& error)
		{
			FILE* handle = fopen(path, "rb");
			if (!handle) { error = std::string("cannot open ") + path; return false; }
			fseek(handle, 0, SEEK_END);
			const long size = ftell(handle);
			fseek(handle, 0, SEEK_SET);
			file.resize(size > 0 ? (size_t)size : 0);
			const size_t got = file.empty() ? 0 : fread(file.data(), 1, file.size(), handle);
			fclose(handle);
			static const char magic[] = "Microsoft C/C++ MSF 7.00\r\n\x1a" "DS\0\0";
			if (got != file.size() || file.size() < 56 || memcmp(file.data(), magic, 32) != 0) { error = "not a PDB (MSF 7.00) file"; return false; }
			blockSize = At<uint32_t>(file, 32);
			const uint32_t blocks = At<uint32_t>(file, 40), directoryBytes = At<uint32_t>(file, 44), mapBlock = At<uint32_t>(file, 52);
			if (blockSize < 512 || (blockSize & (blockSize - 1)) != 0 || (uint64_t)blocks * blockSize > file.size() || mapBlock >= blocks) { error = "broken PDB header"; return false; }
			// The stream directory itself lives in blocks whose numbers are listed in block mapBlock.
			const uint32_t directoryBlocks = (directoryBytes + blockSize - 1) / blockSize;
			if ((uint64_t)directoryBlocks * 4 > blockSize) { error = "PDB directory is too large"; return false; }
			std::vector<uint8_t> directory;
			for (uint32_t i = 0; i < directoryBlocks; i++)
			{
				const uint32_t block = At<uint32_t>(file, (size_t)mapBlock * blockSize + i * 4);
				if (block >= blocks) { error = "broken PDB directory"; return false; }
				directory.insert(directory.end(), file.begin() + (size_t)block * blockSize, file.begin() + (size_t)(block + 1) * blockSize);
			}
			directory.resize(directoryBytes);
			if (directory.size() < 4) { error = "broken PDB directory"; return false; }
			const uint32_t streams = At<uint32_t>(directory, 0);
			size_t at = 4;
			if ((uint64_t)streams * 4 + 4 > directory.size()) { error = "broken PDB directory"; return false; }
			streamSizes.resize(streams);
			for (uint32_t i = 0; i < streams; i++, at += 4) streamSizes[i] = At<uint32_t>(directory, at);
			streamBlocks.resize(streams);
			for (uint32_t i = 0; i < streams; i++)
			{
				const uint32_t size32 = streamSizes[i] == 0xFFFFFFFFu ? 0 : streamSizes[i];
				const uint32_t count = (size32 + blockSize - 1) / blockSize;
				if (at + (uint64_t)count * 4 > directory.size()) { error = "broken PDB directory"; return false; }
				streamBlocks[i].resize(count);
				for (uint32_t b = 0; b < count; b++, at += 4)
				{
					streamBlocks[i][b] = At<uint32_t>(directory, at);
					if (streamBlocks[i][b] >= blocks) { error = "broken PDB directory"; return false; }
				}
			}
			return true;
		}

		bool Stream(uint32_t index, std::vector<uint8_t>& out) const
		{
			out.clear();
			if (index >= streamSizes.size() || streamSizes[index] == 0xFFFFFFFFu) return false;
			out.reserve((size_t)streamBlocks[index].size() * blockSize);
			for (uint32_t block : streamBlocks[index]) out.insert(out.end(), file.begin() + (size_t)block * blockSize, file.begin() + (size_t)(block + 1) * blockSize);
			out.resize(streamSizes[index]);
			return true;
		}
	};
}

// Reads all functions in the file, sorted by address. Spaces in names are replaced with underscores (the same form as in
// a function table file: "std::vector<int,std::allocator<int>_>::...").
inline bool ReadPdbFunctions(const char* path, std::vector<PdbFunction>& functions, PdbIdentity* identity, std::string& error)
{
	using pdb_detail::Reader;
	Reader reader;
	if (!reader.Open(path, error)) return false;
	std::vector<uint8_t> info, dbi, sections;
	if (!reader.Stream(1, info) || info.size() < 28 || !reader.Stream(3, dbi) || dbi.size() < 64) { error = "PDB has no info or DBI stream"; return false; }
	if (identity)
	{
		identity->age = Reader::At<uint32_t>(info, 8);
		memcpy(identity->guid, info.data() + 12, 16);
	}
	const uint32_t modInfoSize = Reader::At<uint32_t>(dbi, 24), contributionSize = Reader::At<uint32_t>(dbi, 28), sectionMapSize = Reader::At<uint32_t>(dbi, 32);
	const uint32_t sourceInfoSize = Reader::At<uint32_t>(dbi, 36), typeServerSize = Reader::At<uint32_t>(dbi, 40), debugHeaderSize = Reader::At<uint32_t>(dbi, 48), ecSize = Reader::At<uint32_t>(dbi, 52);
	const uint64_t debugHeaderAt = 64ull + modInfoSize + contributionSize + sectionMapSize + sourceInfoSize + typeServerSize + ecSize;
	// Sixth number of the optional debug header: the stream with the library's section headers.
	if (debugHeaderSize < 12 || debugHeaderAt + 12 > dbi.size() || 64ull + modInfoSize > dbi.size()) { error = "PDB has no section headers"; return false; }
	const uint16_t sectionStream = Reader::At<uint16_t>(dbi, (size_t)debugHeaderAt + 10);
	if (sectionStream == 0xFFFF || !reader.Stream(sectionStream, sections) || sections.size() < 40) { error = "PDB has no section headers"; return false; }
	std::vector<uint32_t> sectionStart;
	for (size_t at = 0; at + 40 <= sections.size(); at += 40) sectionStart.push_back(Reader::At<uint32_t>(sections, at + 12));

	functions.clear();
	std::vector<uint8_t> symbols;
	size_t at = 64;
	const size_t modulesEnd = 64 + (size_t)modInfoSize;
	while (at + 64 <= modulesEnd)
	{
		const uint16_t stream = Reader::At<uint16_t>(dbi, at + 34);
		const uint32_t symbolBytes = Reader::At<uint32_t>(dbi, at + 36);
		// The fixed part of a module record is followed by two names, each zero-terminated; the record is aligned to 4 bytes.
		size_t next = at + 64;
		for (int name = 0; name < 2; name++)
		{
			while (next < modulesEnd && dbi[next] != 0) next++;
			next++;
		}
		next = (next + 3) & ~(size_t)3;
		at = next;
		if (stream == 0xFFFF || symbolBytes < 4 || !reader.Stream(stream, symbols) || symbols.size() < symbolBytes) continue;
		// First four bytes of the stream: format signature; then records of "length, kind, data".
		for (size_t record = 4; record + 4 <= symbolBytes;)
		{
			const uint16_t length = Reader::At<uint16_t>(symbols, record), kind = Reader::At<uint16_t>(symbols, record + 2);
			const size_t end = record + 2 + length;
			if (length < 2 || end > symbolBytes) break;
			if ((kind == 0x1110 || kind == 0x110f || kind == 0x1147 || kind == 0x1146) && length >= 38)
			{
				const uint32_t size = Reader::At<uint32_t>(symbols, record + 16), offset = Reader::At<uint32_t>(symbols, record + 32);
				const uint16_t section = Reader::At<uint16_t>(symbols, record + 36);
				const char* name = (const char*)symbols.data() + record + 39;
				const size_t room = end - (record + 39);
				const size_t nameLength = strnlen(name, room);
				if (section >= 1 && section <= sectionStart.size() && nameLength > 0 && nameLength < room)
				{
					PdbFunction function = { std::string(name, nameLength), sectionStart[section - 1] + offset, size };
					std::replace(function.name.begin(), function.name.end(), ' ', '_');
					functions.push_back(function);
				}
			}
			record = end;
		}
	}
	if (functions.empty()) { error = "PDB has no function records"; return false; }
	std::stable_sort(functions.begin(), functions.end(), [](const PdbFunction& a, const PdbFunction& b) { return a.rva < b.rva; });
	// The same function is sometimes recorded twice (in two link modules): one record per address and name is kept.
	functions.erase(std::unique(functions.begin(), functions.end(), [](const PdbFunction& a, const PdbFunction& b) { return a.rva == b.rva && a.name == b.name; }), functions.end());
	return true;
}
