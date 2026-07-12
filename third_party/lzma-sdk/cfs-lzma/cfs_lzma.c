#include <stdlib.h>
#include <string.h>
#include "Lzma2Enc.h"
#include "Lzma2Dec.h"
#include "Alloc.h"

#ifdef _WIN32
#define CFS_API __declspec(dllexport)
#else
#define CFS_API
#endif

/* Raw CFS LZMA2 layout: one encoder property byte followed by an LZMA2 stream. */
CFS_API int cfs_lzma2_compress(const Byte *input, size_t input_size, Byte **output, size_t *output_size)
{
  CLzma2EncProps props;
  CLzma2EncHandle encoder;
  size_t capacity;
  size_t written;
  Byte *buffer;
  SRes result;

  if (!output || !output_size || (!input && input_size)) return SZ_ERROR_PARAM;
  if (input_size > (size_t)-1 - 65537) return SZ_ERROR_MEM;
  capacity = input_size + input_size / 8 + 65537;
  buffer = (Byte *)malloc(capacity + 1);
  if (!buffer) return SZ_ERROR_MEM;

  Lzma2EncProps_Init(&props);
  /* Independent CFS blocks favor bounded setup/memory over a huge default dictionary. */
  props.lzmaProps.level = 3;
  props.lzmaProps.dictSize = 1 << 16;
  props.lzmaProps.fb = 32;
  props.lzmaProps.algo = 0;
  props.lzmaProps.numThreads = 1;
  encoder = Lzma2Enc_Create(&g_Alloc, &g_BigAlloc);
  if (!encoder) { free(buffer); return SZ_ERROR_MEM; }
  result = Lzma2Enc_SetProps(encoder, &props);
  if (result == SZ_OK) {
    Lzma2Enc_SetDataSize(encoder, input_size);
    buffer[0] = Lzma2Enc_WriteProperties(encoder);
    written = capacity;
    result = Lzma2Enc_Encode2(encoder, NULL, buffer + 1, &written, NULL, input, input_size, NULL);
    if (result == SZ_OK) { *output = buffer; *output_size = written + 1; }
  }
  Lzma2Enc_Destroy(encoder);
  if (result != SZ_OK) free(buffer);
  return result;
}

CFS_API int cfs_lzma2_decompress(const Byte *input, size_t input_size, Byte *output, size_t output_capacity, size_t *actual_size)
{
  CLzma2Dec decoder;
  SizeT source_size, destination_size;
  ELzmaStatus status;
  SRes result;
  if (!input || input_size < 2 || !output || !actual_size) return SZ_ERROR_PARAM;
  Lzma2Dec_Construct(&decoder);
  result = Lzma2Dec_Allocate(&decoder, input[0], &g_Alloc);
  if (result != SZ_OK) return result;
  Lzma2Dec_Init(&decoder);
  source_size = input_size - 1;
  destination_size = output_capacity;
  result = Lzma2Dec_DecodeToBuf(&decoder, output, &destination_size, input + 1, &source_size, LZMA_FINISH_END, &status);
  Lzma2Dec_Free(&decoder, &g_Alloc);
  if (result == SZ_OK && (source_size != input_size - 1 || destination_size != output_capacity)) return SZ_ERROR_DATA;
  *actual_size = destination_size;
  return result;
}

CFS_API void cfs_lzma_free(void *buffer) { free(buffer); }
