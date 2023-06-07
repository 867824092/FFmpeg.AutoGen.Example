using FFmpeg.AutoGen;
using FFmpeg.Helper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FFmpeg.Video {
    public class FFmpegVideo {
        static long pts = 0;
        public static unsafe void Run(string inputUrl,string outputUrl,CancellationToken token) {
#if DEBUG
            Console.WriteLine("Current directory: " + Environment.CurrentDirectory);
            Console.WriteLine("Running in {0}-bit mode.", Environment.Is64BitProcess ? "64" : "32");
            Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");
            Console.WriteLine();
#endif
            AVFormatContext* input_format_context = null;
            AVFormatContext* output_format_context = null;
            AVCodecContext* input_codec_conetxt = null;
            AVCodecContext* output_codec_conetxt = null;
            AVStream* out_stream = null;
            AVPacket* packet = ffmpeg.av_packet_alloc();
            AVPacket* outPkt = ffmpeg.av_packet_alloc();
            AVFrame* srcFrame = ffmpeg.av_frame_alloc();
            AVFrame* pFrameYUV = ffmpeg.av_frame_alloc();
            int streamIndex = -1;
            //图像转换上下文
            SwsContext* sws_context = null;
            int ret = 0;
            if (Open_input_file(inputUrl,&input_format_context, &input_codec_conetxt, ref streamIndex) < 0) {
                goto cleanup;
            }
            if (Open_output_file(outputUrl, &input_format_context,
                    &output_format_context, &output_codec_conetxt, &out_stream) < 0) {
                goto cleanup;
            }

            if (Init_SwsContext(&input_codec_conetxt, &output_codec_conetxt, &sws_context) < 0) {
                Console.WriteLine("Could not initialize the conversion context");
                goto cleanup;
            }

            // 计算一帧的大小
            int out_buffer_size = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_YUV420P,
                input_codec_conetxt->width, input_codec_conetxt->height, 1);
            byte* out_buffer = (byte*)ffmpeg.av_malloc((ulong)out_buffer_size);
            byte_ptrArray4* ptrFrameData = (byte_ptrArray4*)&pFrameYUV->data;
            int_array4* ptrLineSize = (int_array4*)&pFrameYUV->linesize;
            ret = ffmpeg.av_image_fill_arrays(ref *ptrFrameData, ref *ptrLineSize,
                out_buffer, AVPixelFormat.AV_PIX_FMT_YUV420P,
                input_codec_conetxt->width, input_codec_conetxt->height, 1);
            if (ret < 0) {
                Console.WriteLine("Could not fill image arrays");
                goto cleanup;
            }
            pFrameYUV->format = (int)output_codec_conetxt->pix_fmt;
            pFrameYUV->width = output_codec_conetxt->width;
            pFrameYUV->height = output_codec_conetxt->height;
            AVDictionary* opt = null;
            //写入文件头
            ffmpeg.avformat_write_header(output_format_context, null);
            int frameCount = 0;
            while (!token.IsCancellationRequested) {
                ret = ffmpeg.av_read_frame(input_format_context, packet);
                if (ret < 0 || ret == ffmpeg.AVERROR_EOF) {
                    break;
                }
                if (packet->stream_index == streamIndex) {
                    if (ffmpeg.avcodec_send_packet(input_codec_conetxt, packet) >= 0) {
                        if ((ret = ffmpeg.avcodec_receive_frame(input_codec_conetxt, srcFrame)) >= 0) {
                            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) {
                                goto cleanup;
                            }
                            else
                                if (ret < 0) {
                                goto cleanup;
                            }

                            ret = ffmpeg.sws_scale(sws_context, (srcFrame)->data, (srcFrame)->linesize, 0, (input_codec_conetxt)->height, (pFrameYUV)->data, (pFrameYUV)->linesize);
                            if (ret < 0) {
                                Console.WriteLine("Could not scale the frame");
                                goto cleanup;
                            }
                            pFrameYUV->pts = srcFrame->pts;
                            if (ffmpeg.avcodec_send_frame(output_codec_conetxt, pFrameYUV) >= 0) {
                                ret = ffmpeg.avcodec_receive_packet(output_codec_conetxt, outPkt);
                                if (ret >= 0) {
                                    //在FFMPEG中可以区分 视频流 和 音频流。
                                    //同时每个媒体流 都有一个时间基数，
                                    //一般的MP4 的视频流的时间基数是90000，
                                    //这个时间基数 在封装的时候可以自己定义，
                                    //这个时间基数可以这么理解，
                                    //一把尺子一厘米包含了10个小刻度，
                                    //同理，时间基数 是指 1秒钟 包含了 90000 个 刻度，
                                    //如果是固定帧率的情况下，假设 设计的视频流 的帧率是 15，
                                    //那么一帧视频 就 占用了 90000 / 15 = 6000 个时间刻度，
                                    //那么FFMPEG 中 视频流的时间戳的计算 pts = frame_no（当前帧数） *6000，
                                    //如果 数据中没有 B帧 数据，那么可以认为 dts 和 pts 相同，如果是 可变帧率 ，
                                    //就需要统计每秒的帧数，来计算 一帧 数据占用的刻度数，由于 每一帧占的刻度数不一样了，
                                    //不能简单的 用帧数 *刻度数，而是 使用累加 计算 pts += 90000 / 帧数，dts 和 pts 相同。

                                    //outPkt->pts = outPkt->dts = frameCount *  (out_stream->time_base.den / 30);
                                    //frameCount++;
                                    //Console.WriteLine(outPkt->pts);
                                    outPkt->pts = outPkt->dts = pts;
                                    pts += out_stream->time_base.den / 30;
                                    //Console.WriteLine(pts);
                                    outPkt->stream_index = streamIndex;
                                    outPkt->pos = -1;
                                    ffmpeg.av_interleaved_write_frame(output_format_context, outPkt);
                                    ffmpeg.av_packet_unref(outPkt);
                                }
                            }
                        }
                    }
                    ffmpeg.av_packet_unref(packet);
                }
            }
            ffmpeg.av_write_trailer(output_format_context);

        cleanup:
            ffmpeg.av_packet_unref(packet);
            ffmpeg.av_frame_free(&srcFrame);
            ffmpeg.av_frame_free(&pFrameYUV);

            ffmpeg.avcodec_free_context(&input_codec_conetxt);
            ffmpeg.avcodec_close(input_codec_conetxt);

            ffmpeg.avformat_close_input(&input_format_context);
            ffmpeg.avformat_free_context(input_format_context);

            ffmpeg.av_packet_unref(outPkt);

            ffmpeg.avcodec_free_context(&output_codec_conetxt);

            ffmpeg.avio_closep(&output_format_context->pb);
            ffmpeg.avformat_free_context(output_format_context);

        }
        static unsafe int Open_input_file(string url,AVFormatContext** input_format_context,
            AVCodecContext** input_codec_context, ref int streamIndex) {
            AVCodec* input_codec;
            AVInputFormat* input_format = ffmpeg.av_find_input_format("dshow");
            int ret = ffmpeg.avformat_open_input(input_format_context, url, input_format, null);
            if (ret < 0) {
                Console.WriteLine($"Could not open input file 'video=Integrated Camera' (error '{FFmpegHelper.av_err2str(ret)}')");
                *input_format_context = null;
                return ret;
            }
            if ((ret = ffmpeg.avformat_find_stream_info(*input_format_context, null)) < 0) {
                Console.WriteLine($"Could not find stream info (error '{FFmpegHelper.av_err2str(ret)}')");
                ffmpeg.avformat_close_input(input_format_context);
                return ret;
            }

            for (int i = 0; i < (*input_format_context)->nb_streams; i++) {
                if ((*input_format_context)->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO) {
                    streamIndex = i;
                    break;
                }
            }
            uint number_of_streams = (*input_format_context)->nb_streams;
            if (number_of_streams != 1) {
                Console.WriteLine($"Expected one audio input stream, but found {number_of_streams}");
                ffmpeg.avformat_close_input(input_format_context);
                return ffmpeg.AVERROR_EXIT;
            }

            AVCodecParameters* codecpar = (*input_format_context)->streams[0]->codecpar;
            if ((input_codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id)) == null) {
                Console.WriteLine("Could not find input codec");
                ffmpeg.avformat_close_input(input_format_context);
                return ffmpeg.AVERROR_EXIT;
            }

            AVCodecContext* avcCtx;
            avcCtx = ffmpeg.avcodec_alloc_context3(input_codec);
            if (avcCtx == null) {
                Console.WriteLine("Could not allocate a decoding context");
                ffmpeg.avformat_close_input(input_format_context);
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            ret = ffmpeg.avcodec_parameters_to_context(avcCtx, codecpar);
            if (ret < 0) {
                ffmpeg.avformat_close_input(input_format_context);
                ffmpeg.avcodec_free_context(&avcCtx);
                return ret;
            }

            if ((ret = ffmpeg.avcodec_open2(avcCtx, input_codec, null)) < 0) {
                Console.WriteLine($"Could not open input codec (error '{FFmpegHelper.av_err2str(ret)}')");
                ffmpeg.avcodec_free_context(&avcCtx);
                ffmpeg.avformat_close_input(input_format_context);
                return ret;
            }

            *input_codec_context = avcCtx;
            return 0;
        }

        static unsafe int Open_output_file(string fileName, AVFormatContext** input_format_context,
            AVFormatContext** output_format_context, AVCodecContext** output_codec_context, AVStream** out_stream) {
            AVCodecContext* avcCtx;
            AVCodec* output_codec;
            AVStream* stream = null;
            int ret = 0;
            ret = ffmpeg.avformat_alloc_output_context2(output_format_context, null, null, fileName);
            if (ret < 0) {
                Console.WriteLine("Could not allocate output format context");
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }
            ret = ffmpeg.avio_open(&(*output_format_context)->pb, fileName, ffmpeg.AVIO_FLAG_WRITE);
            if (ret < 0) {
                Console.WriteLine($"Could not open output file '{fileName}' (error '{FFmpegHelper.av_err2str(ret)}')");
                return ret;
            }
            output_codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
            if (output_codec == null) {
                Console.WriteLine("Could not find an encoder for 'AV_CODEC_ID_H264'.");
                return ffmpeg.AVERROR_EXIT;
            }

            avcCtx = ffmpeg.avcodec_alloc_context3(output_codec);
            if (avcCtx == null) {
                Console.WriteLine("Could not allocate an encoding context.");
                return ffmpeg.AVERROR_EXIT;
            }
            avcCtx->codec_id = AVCodecID.AV_CODEC_ID_H264;
            avcCtx->bit_rate = 2000000;
            avcCtx->width = (*input_format_context)->streams[0]->codecpar->width;
            avcCtx->height = (*input_format_context)->streams[0]->codecpar->height;
            AVRational time_base = new AVRational { num = 1, den = 30 };
            avcCtx->time_base = time_base;
            avcCtx->gop_size = 10;
            avcCtx->max_b_frames = 3;
            avcCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            if (avcCtx->codec_id == AVCodecID.AV_CODEC_ID_H264) {
                ffmpeg.av_opt_set(avcCtx->priv_data, "preset", "slow", 0);
                ffmpeg.av_opt_set(avcCtx->priv_data, "tune", "zerolatency", 0);
            }
            if (((*output_format_context)->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) == ffmpeg.AVFMT_GLOBALHEADER) {
                avcCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            ret = ffmpeg.avcodec_open2(avcCtx, output_codec, null);
            if (ret < 0) {
                Console.WriteLine($"Could not open output codec (error '{FFmpegHelper.av_err2str(ret)}')");
                goto cleanup;
            }

            stream = ffmpeg.avformat_new_stream(*output_format_context, output_codec);
            if (stream == null) {
                Console.WriteLine("Could not allocate stream.");
                ret = ffmpeg.AVERROR_EXIT;
                goto cleanup;
            }
            //stream->time_base = time_base;
            ret = ffmpeg.avcodec_parameters_from_context(stream->codecpar, avcCtx);
            if (ret < 0) {
                Console.WriteLine($"Could not initialize stream parameters (error '{FFmpegHelper.av_err2str(ret)}')");
                goto cleanup;
            }

            *output_codec_context = avcCtx;
            *out_stream = stream;
            return ret;

        cleanup:
            ffmpeg.avio_closep(&(*output_format_context)->pb);
            ffmpeg.avformat_free_context(*output_format_context);
            *output_format_context = null;
            return ret < 0 ? ret : ffmpeg.AVERROR_EXIT;
        }

        static unsafe int Init_SwsContext(AVCodecContext** input_codec_context, AVCodecContext** output_codec_context,
            SwsContext** sws_context) {
            SwsContext* sws_ctx = ffmpeg.sws_getContext((*input_codec_context)->width,
                (*input_codec_context)->height,
                (*input_codec_context)->pix_fmt,
                (*input_codec_context)->width,
                (*input_codec_context)->height,
                AVPixelFormat.AV_PIX_FMT_YUV420P,
                ffmpeg.SWS_BICUBIC, null, null, null);
            if (sws_ctx == null) {
                Console.WriteLine("Could not initialize the conversion context");
                return ffmpeg.AVERROR(ffmpeg.ENOMEM);
            }

            *sws_context = sws_ctx;
            return 0;
        }
    }
}
